$ErrorActionPreference = 'Stop'

$enginePath = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$testsPath = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'
$engine = Get-Content -Raw $enginePath
$tests = Get-Content -Raw $testsPath

function Replace-ExactlyOnce([string]$text, [string]$pattern, [string]$replacement, [string]$label) {
    $matches = [regex]::Matches($text, $pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if ($matches.Count -ne 1) {
        throw "$label expected exactly one match, found $($matches.Count)."
    }
    return [regex]::Replace($text, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement }, [System.Text.RegularExpressions.RegexOptions]::Singleline)
}

$writerReplacement = @'
    private static async Task WriterLoopAsync(
        DestinationWorker worker,
        CopyOptions options,
        CopyJob job)
    {
        CurrentFile? current = null;
        using var recovery = new RecoveryCheckpointWriter(worker.Root);
        try
        {
            worker.Progress.SetPhase(DestinationPhase.Copying);
            await foreach (var message in worker.Channel.Reader.ReadAllAsync())
            {
                worker.DecrementQueueDepth();
                var controlDelivery = message as ControlDelivery;
                var effectiveMessage = controlDelivery?.Message ?? message;
                var data = effectiveMessage as DataMessage;
                var dataOwnedByWriter = data is not null;
                if (data is not null)
                    worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
                try
                {
                    if (!worker.IsActive)
                        continue;

                    job.Token.ThrowIfCancellationRequested();
                    await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
                    switch (effectiveMessage)
                    {
                        case BeginMessage begin:
                            if (current is not null)
                                throw new InvalidOperationException("Se recibió Begin antes de cerrar el archivo anterior.");
                            current = BeginFile(worker, begin.Entry);
                            if (current.DirectSession is not null)
                                job.Telemetry.RecordDirectDestinationFile();
                            else if (current.DirectRequested)
                                job.Telemetry.RecordDirectDestinationFallback();
                            break;

                        case DataMessage chunkData when current is not null:
                            if (current.Failed)
                                break;

                            if (current.DirectFallbackRequested)
                            {
                                var fallbackError = await DrainPendingWritesAsync(worker, current, job).ConfigureAwait(false);
                                if (fallbackError is not null)
                                {
                                    FailCurrentFile(worker, current, options, fallbackError.Message);
                                    break;
                                }
                            }

                            var offset = current.ReserveWriteOffset(chunkData.Block.Length);
                            current.VerificationBlocks.Add(
                                new VerificationBlock(chunkData.Block.Length, chunkData.Block.VerificationCrc32));
                            current.PendingWrites.Add(
                                WriteBlockAtOffsetAsync(worker, current, chunkData.Block, offset, job));
                            dataOwnedByWriter = false;
                            PruneCompletedSuccesses(current);
                            break;

                        case DataMessage:
                            break;

                        case EndMessage end when current is not null:
                            var pendingError = await DrainPendingWritesAsync(worker, current, job).ConfigureAwait(false);
                            if (pendingError is not null)
                                FailCurrentFile(worker, current, options, pendingError.Message);
                            if (!current.Failed)
                                FinishFile(worker, current, end.Hash, options, recovery, job);
                            current = null;
                            break;
                    }
                    worker.NoteProgress();
                }
                finally
                {
                    if (dataOwnedByWriter)
                        data?.Block.Release();
                    controlDelivery?.ReleaseBudget();
                }
            }
        }
        catch (OperationCanceledException)
        {
            worker.Progress.SetPhase(DestinationPhase.Cancelled, "Cancelado");
        }
        catch (Exception ex)
        {
            worker.Fail(ex.Message);
        }
        finally
        {
            if (current is not null)
            {
                await ReleasePendingWritesAsync(current).ConfigureAwait(false);
                current.Stream?.Dispose();
                current.DirectSession?.Dispose();
                TryDelete(current.PartPath);
                if (current.Copied > 0 && !current.Failed)
                    worker.Progress.RollbackWritten((ulong)current.Copied);
            }
            DrainAndRelease(worker);
        }
    }

    private static CurrentFile BeginFile
'@

$engine = Replace-ExactlyOnce $engine '(?ms)^    private static async Task WriterLoopAsync\(.*?^    private static CurrentFile BeginFile' $writerReplacement 'WriterLoopAsync'

$writeHelpers = @'
    private static async Task<PendingWriteResult> WriteBlockAtOffsetAsync(
        DestinationWorker worker,
        CurrentFile current,
        SharedBlock block,
        long offset,
        CopyJob job)
    {
        var data = block.Memory;
        Exception? last = null;
        var direct = current.DirectSession;

        if (direct is not null)
        {
            try
            {
                job.Token.ThrowIfCancellationRequested();
                await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
                if (!block.IsAlignedFor(direct.Alignment))
                {
                    var alignmentError = new InvalidOperationException("El bloque FAN-OUT no conserva la alineación requerida por Direct I/O.");
                    current.RequestDirectFallback();
                    return PendingWriteResult.NeedsBufferedRetry(block, offset, alignmentError);
                }

                var queueDepth = StorageWritePolicy.LargeWriteQueueDepth(
                    worker.Device,
                    worker.DeviceScheduler.ExplorationQueueDepth,
                    data.Length);
                var started = Stopwatch.GetTimestamp();
                int operations;
                try
                {
                    operations = await direct.WriteAsync(
                        data,
                        offset,
                        current.Entry.Size,
                        payloadIsAligned: true,
                        queueDepth,
                        StorageWritePolicy.MinimumParallelSliceBytes,
                        worker.DeviceScheduler,
                        job.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (DirectIoDestinationWriter.IsFallbackable(ex))
                {
                    current.RequestDirectFallback();
                    return PendingWriteResult.NeedsBufferedRetry(block, offset, ex);
                }

                job.Telemetry.RecordDirectDestinationWrite(data.Length, operations);
                for (var operation = 0; operation < operations; operation++)
                    job.Telemetry.RecordWriteOperation();
                job.Telemetry.RecordWrite(data.Length, Stopwatch.GetElapsedTime(started));
                current.RecordCompletedWrite(data.Length);
                worker.Progress.AddWritten(data.Length);
                worker.NoteProgress();
                block.Release();
                return PendingWriteResult.Success();
            }
            catch (Exception ex)
            {
                block.Release();
                return PendingWriteResult.Failed(ex);
            }
        }

        for (var attempt = 0; attempt <= Retries; attempt++)
        {
            try
            {
                job.Token.ThrowIfCancellationRequested();
                await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
                var stream = current.Stream ?? throw new IOException($"No existe handle buffered para {current.Entry.RelativePath}.");
                var queueDepth = StorageWritePolicy.LargeWriteQueueDepth(
                    worker.Device,
                    worker.DeviceScheduler.ExplorationQueueDepth,
                    data.Length);
                var started = Stopwatch.GetTimestamp();
                var operations = await DestinationWriteCoordinator.WriteAsync(
                    stream.SafeFileHandle,
                    data,
                    offset,
                    queueDepth,
                    StorageWritePolicy.MinimumParallelSliceBytes,
                    worker.DeviceScheduler,
                    job.Token).ConfigureAwait(false);

                for (var operation = 0; operation < operations; operation++)
                    job.Telemetry.RecordWriteOperation();
                job.Telemetry.RecordWrite(data.Length, Stopwatch.GetElapsedTime(started));
                current.RecordCompletedWrite(data.Length);
                worker.Progress.AddWritten(data.Length);
                worker.NoteProgress();
                block.Release();
                return PendingWriteResult.Success();
            }
            catch (Exception ex)
            {
                last = ex;
                if (ex is OperationCanceledException || attempt >= Retries)
                    break;
                worker.Progress.AddRetry();
                try
                {
                    await Task.Delay(75 * (attempt + 1), job.Token).ConfigureAwait(false);
                }
                catch (Exception delayError)
                {
                    last = delayError;
                    break;
                }
            }
        }

        block.Release();
        return PendingWriteResult.Failed(
            new IOException($"No se pudo escribir {current.Entry.RelativePath} en offset {offset} después de reintentos.", last));
    }

    private static void PruneCompletedSuccesses(CurrentFile current)
    {
        for (var index = current.PendingWrites.Count - 1; index >= 0; index--)
        {
            var pending = current.PendingWrites[index];
            if (pending.IsCompletedSuccessfully && pending.Result.Status == PendingWriteStatus.Success)
                current.PendingWrites.RemoveAt(index);
        }
    }

    private static async Task<Exception?> DrainPendingWritesAsync(
        DestinationWorker worker,
        CurrentFile current,
        CopyJob job)
    {
        if (current.PendingWrites.Count == 0)
            return null;

        var pending = current.PendingWrites.ToArray();
        current.PendingWrites.Clear();
        var results = await Task.WhenAll(pending).ConfigureAwait(false);

        var failure = results.FirstOrDefault(result => result.Status == PendingWriteStatus.Failed);
        if (failure is not null)
        {
            foreach (var retry in results.Where(result => result.Status == PendingWriteStatus.NeedsBufferedRetry))
                retry.RetryBlock?.Release();
            return failure.Error ?? new IOException($"Falló una escritura pendiente de {current.Entry.RelativePath}.");
        }

        var fallback = results
            .Where(result => result.Status == PendingWriteStatus.NeedsBufferedRetry)
            .ToArray();
        if (fallback.Length == 0)
        {
            current.ClearDirectFallbackRequest();
            return null;
        }

        try
        {
            SwitchToBufferedAfterDrain(current, job);
        }
        catch (Exception ex)
        {
            foreach (var retry in fallback)
                retry.RetryBlock?.Release();
            return ex;
        }

        current.ClearDirectFallbackRequest();
        var retries = fallback.Select(result =>
        {
            worker.Progress.AddRetry();
            return WriteBlockAtOffsetAsync(
                worker,
                current,
                result.RetryBlock ?? throw new InvalidOperationException("Fallback sin bloque retenido."),
                result.Offset,
                job);
        }).ToArray();
        var retryResults = await Task.WhenAll(retries).ConfigureAwait(false);
        var retryFailure = retryResults.FirstOrDefault(result => result.Status != PendingWriteStatus.Success);
        return retryFailure?.Error;
    }

    private static async Task ReleasePendingWritesAsync(CurrentFile current)
    {
        if (current.PendingWrites.Count == 0)
            return;
        var pending = current.PendingWrites.ToArray();
        current.PendingWrites.Clear();
        var results = await Task.WhenAll(pending).ConfigureAwait(false);
        foreach (var result in results)
            result.RetryBlock?.Release();
    }

    private static void SwitchToBufferedAfterDrain(CurrentFile current, CopyJob job)
    {
        if (current.DirectSession is null)
            return;
        current.DirectSession.Dispose();
        current.DirectSession = null;
        current.DirectEnabled = false;
        job.Telemetry.RecordDirectDestinationFallback();

        using (var normalize = new FileStream(current.PartPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            normalize.SetLength(current.Entry.Size);
            normalize.Flush(flushToDisk: true);
        }
        current.Stream = ReopenPart(current.PartPath);
    }

    private static void FailCurrentFile(
        DestinationWorker worker,
        CurrentFile current,
        CopyOptions options,
        string error)
    {
        if (current.Failed)
            return;
        current.Failed = true;
        current.Stream?.Dispose();
        current.Stream = null;
        current.DirectSession?.Dispose();
        current.DirectSession = null;
        TryDelete(current.PartPath);
        if (current.Copied > 0)
            worker.Progress.RollbackWritten((ulong)current.Copied);
        if (options.KeepGoing)
            worker.Progress.MarkError(error);
        else
            worker.Fail(error);
    }


    private static void FinishFile(
'@

$engine = Replace-ExactlyOnce $engine '(?ms)^    private static async Task WriteWithRetryAsync\(.*?^    private static void FinishFile\(' $writeHelpers 'destination write path'

# All destination writes are explicit-offset now; remove stale stream-position plumbing.
$engine = $engine.Replace('using (OpenPartStream(part, FileMode.CreateNew, offset: 0, preallocationSize)) { }', 'using (OpenPartStream(part, FileMode.CreateNew, preallocationSize)) { }')
$engine = $engine.Replace('stream = ReopenPart(part, 0);', 'stream = ReopenPart(part);')
$engine = $engine.Replace('stream = OpenPartStream(part, FileMode.CreateNew, offset: 0, preallocationSize);', 'stream = OpenPartStream(part, FileMode.CreateNew, preallocationSize);')

$streamHelpers = @'
    private static FileStream ReopenPart(string path) =>
        OpenPartStream(path, FileMode.Open, preallocationSize: 0);

    private static FileStream OpenPartStream(
        string path,
        FileMode mode,
        long preallocationSize)
    {
        var options = FileOptions.Asynchronous | FileOptions.SequentialScan;
        return new FileStream(path, new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = options,
            BufferSize = 1,
            PreallocationSize = preallocationSize,
        });
    }

    private static void DrainAndRelease
'@
$engine = Replace-ExactlyOnce $engine '(?ms)^    private static FileStream ReopenPart\(.*?^    private static void DrainAndRelease' $streamHelpers 'stream helpers'

# Finish requires both scheduling and completion to cover the exact file.
$engine = $engine.Replace(
'        if (current.Copied != current.Entry.Size)`r`n        {',
'        if (current.ScheduledBytes != current.Entry.Size || current.Copied != current.Entry.Size)`r`n        {')
$engine = $engine.Replace(
'        if (current.Copied != current.Entry.Size)`n        {',
'        if (current.ScheduledBytes != current.Entry.Size || current.Copied != current.Entry.Size)`n        {')

$currentFileReplacement = @'
    private enum PendingWriteStatus
    {
        Success,
        NeedsBufferedRetry,
        Failed,
    }

    private sealed record PendingWriteResult(
        PendingWriteStatus Status,
        SharedBlock? RetryBlock,
        long Offset,
        Exception? Error)
    {
        internal static PendingWriteResult Success() =>
            new(PendingWriteStatus.Success, null, 0, null);

        internal static PendingWriteResult NeedsBufferedRetry(SharedBlock block, long offset, Exception error) =>
            new(PendingWriteStatus.NeedsBufferedRetry, block, offset, error);

        internal static PendingWriteResult Failed(Exception error) =>
            new(PendingWriteStatus.Failed, null, 0, error);
    }

    private sealed class CurrentFile(
        FileEntry entry,
        string destinationPath,
        string partPath,
        string backupPath,
        FileStream? stream,
        DirectIoDestinationWriter.Session? directSession,
        bool directRequested)
    {
        private long _copied;
        private int _directFallbackRequested;

        public List<VerificationBlock> VerificationBlocks { get; } = [];
        public List<Task<PendingWriteResult>> PendingWrites { get; } = [];
        public FileEntry Entry { get; } = entry;
        public string DestinationPath { get; } = destinationPath;
        public string PartPath { get; } = partPath;
        public string BackupPath { get; } = backupPath;
        public FileStream? Stream { get; set; } = stream;
        public DirectIoDestinationWriter.Session? DirectSession { get; set; } = directSession;
        public bool DirectRequested { get; } = directRequested;
        public bool DirectEnabled { get; set; } = directSession is not null;
        public long ScheduledBytes { get; private set; }
        public long Copied => Interlocked.Read(ref _copied);
        public bool DirectFallbackRequested => Volatile.Read(ref _directFallbackRequested) != 0;
        public bool Failed { get; set; }

        public long ReserveWriteOffset(int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            var offset = ScheduledBytes;
            var next = checked(offset + length);
            if (next > Entry.Size)
                throw new IOException($"La tubería intentó programar más bytes que el tamaño de {Entry.RelativePath}.");
            ScheduledBytes = next;
            return offset;
        }

        public void RecordCompletedWrite(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            var completed = Interlocked.Add(ref _copied, bytes);
            if (completed > Entry.Size)
                throw new IOException($"La tubería completó más bytes que el tamaño de {Entry.RelativePath}.");
        }

        public void RequestDirectFallback() => Interlocked.Exchange(ref _directFallbackRequested, 1);
        public void ClearDirectFallbackRequest() => Interlocked.Exchange(ref _directFallbackRequested, 0);
    }
}
'@
$engine = Replace-ExactlyOnce $engine '(?ms)^    private sealed class CurrentFile\(.*?^\}' $currentFileReplacement 'CurrentFile'

# Architecture contract: the old block-at-a-time await path must not return.
$contract = @'

    [TestMethod]
    public void DestinationWriterKeepsMultipleBlocksInFlightWithExplicitOffsets()
    {
        var engineMethods = typeof(CopyEngine)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(engineMethods, "WriteWithRetryAsync");
        CollectionAssert.DoesNotContain(engineMethods, "SwitchToBuffered");
        CollectionAssert.DoesNotContain(engineMethods, "ResetPartLength");
        CollectionAssert.Contains(engineMethods, "WriteBlockAtOffsetAsync");
        CollectionAssert.Contains(engineMethods, "DrainPendingWritesAsync");
        CollectionAssert.Contains(engineMethods, "PruneCompletedSuccesses");
        CollectionAssert.Contains(engineMethods, "SwitchToBufferedAfterDrain");

        var currentFile = typeof(CopyEngine).GetNestedType("CurrentFile", BindingFlags.NonPublic);
        Assert.IsNotNull(currentFile);
        var properties = currentFile
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.Contains(properties, "PendingWrites");
        CollectionAssert.Contains(properties, "ScheduledBytes");
        CollectionAssert.Contains(properties, "Copied");
        CollectionAssert.Contains(properties, "DirectFallbackRequested");

        var reserve = currentFile.GetMethod("ReserveWriteOffset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(reserve);
        var record = currentFile.GetMethod("RecordCompletedWrite", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(record);
    }
'@
if ($tests -notmatch 'DestinationWriterKeepsMultipleBlocksInFlightWithExplicitOffsets') {
    $lastBrace = $tests.LastIndexOf('}')
    if ($lastBrace -lt 0) { throw 'UnificationContractTests closing brace not found.' }
    $tests = $tests.Substring(0, $lastBrace) + $contract + "`n}" + $tests.Substring($lastBrace + 1)
}

# Stale-route gates in the product source itself.
foreach ($stale in @('WriteWithRetryAsync', 'SwitchToBuffered(', 'ResetPartLength(', 'ReopenPart(part, 0)', 'OpenPartStream(part, FileMode.CreateNew, offset:')) {
    if ($engine.Contains($stale)) { throw "Stale destination write route remains: $stale" }
}

Set-Content -Path $enginePath -Value $engine -NoNewline
Set-Content -Path $testsPath -Value $tests -NoNewline
