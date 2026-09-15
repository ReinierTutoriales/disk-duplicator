using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using Blake3;

namespace RepartoCopier.Core;

public sealed class CopyJob : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly AsyncPauseGate _pauseGate = new();
    private readonly IReadOnlyList<DestinationProgress> _progress;
    private readonly CopyTelemetry _telemetry = new();
    private Task _completion = Task.CompletedTask;

    internal CopyJob(IReadOnlyList<DestinationProgress> progress) => _progress = progress;

    public bool IsPaused => _pauseGate.IsPaused;
    public Task Completion => _completion;
    internal CancellationToken Token => _cancel.Token;

    internal void Attach(Task completion) => _completion = completion;

    public IReadOnlyList<DestinationSnapshot> Snapshot() =>
        _progress.Select(item => item.Snapshot()).ToArray();

    public CopyDiagnosticsSnapshot DiagnosticsSnapshot() => _telemetry.Snapshot();
    internal CopyTelemetry Telemetry => _telemetry;

    public void SetPaused(bool paused) => _pauseGate.SetPaused(paused);

    public void RequestCancel()
    {
        _pauseGate.SetPaused(false);
        _cancel.Cancel();
    }

    internal ValueTask WaitIfPausedAsync(CancellationToken token) => _pauseGate.WaitAsync(token);

    public async ValueTask DisposeAsync()
    {
        RequestCancel();
        try { await _completion.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cancel.Dispose();
    }

    private sealed class AsyncPauseGate
    {
        private readonly object _gate = new();
        private TaskCompletionSource? _resumeSignal;

        public bool IsPaused
        {
            get { lock (_gate) return _resumeSignal is not null; }
        }

        public void SetPaused(bool paused)
        {
            TaskCompletionSource? resume = null;
            lock (_gate)
            {
                if (paused)
                {
                    _resumeSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                else
                {
                    resume = _resumeSignal;
                    _resumeSignal = null;
                }
            }
            resume?.TrySetResult();
        }

        public async ValueTask WaitAsync(CancellationToken token)
        {
            while (true)
            {
                Task? wait;
                lock (_gate)
                    wait = _resumeSignal?.Task;
                if (wait is null)
                    return;
                await wait.WaitAsync(token).ConfigureAwait(false);
            }
        }
    }
}

public static class CopyEngine
{

    private const long InitialBufferBudget = 512L * 1024 * 1024;


    private const int Retries = 2;

    public static CopyJob Start(CopyPlan plan, CopyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new CopyOptions(
                    Verify: false,
                    SkipSame: plan.SkipSame,
                    KeepGoing: plan.KeepGoing);

        var prepared = Preflight(plan);
        try
        {
            var progress = prepared.DestinationRoots
                .Select(root => new DestinationProgress(root, prepared.TotalBytes, (ulong)prepared.Files.Count))
                .ToArray();
            var job = new CopyJob(progress);
            job.Attach(Task.Run(() => RunAsync(prepared, progress, options, job), CancellationToken.None));
            return job;
        }
        catch
        {
            prepared.ReleaseStateLeases();
            throw;
        }
    }

    public static async Task<CopyJob> StartAsync(
        CopyPlan plan,
        CopyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new CopyOptions(
                    Verify: false,
                    SkipSame: plan.SkipSame,
                    KeepGoing: plan.KeepGoing);

        var prepared = await Task.Run(() => Preflight(plan), cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = prepared.DestinationRoots
                .Select(root => new DestinationProgress(root, prepared.TotalBytes, (ulong)prepared.Files.Count))
                .ToArray();
            var job = new CopyJob(progress);
            job.Attach(Task.Run(() => RunAsync(prepared, progress, options, job), CancellationToken.None));
            return job;
        }
        catch
        {
            prepared.ReleaseStateLeases();
            throw;
        }
    }

    private static PreparedCopy Preflight(CopyPlan plan)
    {
        var requestedSource = Path.GetFullPath(plan.Source);
        var sourceIsDirectory = Directory.Exists(requestedSource);
        var sourceIsFile = File.Exists(requestedSource);
        if (!sourceIsDirectory && !sourceIsFile)
            throw new IOException("El origen debe ser un archivo regular o una carpeta existente.");
        if (WindowsPath.IsReparsePoint(requestedSource))
            throw new IOException("El origen no puede ser un symlink/junction/reparse point.");

        var source = PreflightSafety.CanonicalExisting(requestedSource, "origen");
        var sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        if (string.IsNullOrWhiteSpace(sourceName))
            throw new IOException("El origen debe tener un nombre; no se puede duplicar una raíz completa.");

        var effectiveDestinations = plan.Destinations
            .Select(Path.GetFullPath)
            .Select(basePath => sourceIsDirectory ? Path.Combine(basePath, sourceName) : basePath)
            .ToArray();
        var destinationRoots = PreflightSafety.ValidateAndCanonicalizeDestinations(
            source,
            effectiveDestinations);

        var destinationTopology = StorageTopology.InspectDestinations(destinationRoots);
        var destinationDevices = destinationTopology.Destinations.ToArray();
        if (destinationDevices.Length != destinationRoots.Length)
            throw new IOException("La topología de almacenamiento no coincide con los destinos preparados.");
        var sourceDevice = StorageTopology.InspectDestinations([source]).Destinations.Single();

        var sourceRoot = sourceIsDirectory
            ? source
            : Path.GetDirectoryName(source)
                ?? throw new IOException("El archivo de origen no tiene carpeta padre.");

        SourceTreeScan scan;
        if (sourceIsDirectory)
        {
            scan = PreflightSafety.ScanDirectory(source);
        }
        else
        {
            RejectReparse(source, "archivo de origen");
            var info = new FileInfo(source);
            scan = new SourceTreeScan(
                [new ScannedFile(
                    source,
                    Path.GetFileName(source),
                    info.Length,
                    info.LastWriteTimeUtc)],
                []);
        }

        var files = scan.Files
            .Select(file => new FileEntry(
                file.FullPath,
                file.RelativePath,
                file.Size,
                file.LastWriteTimeUtc,
                ToUnixNanoseconds(file.LastWriteTimeUtc)))
            .ToList();
        var directories = scan.Directories.ToList();
        var totalBytes = files.Aggregate<FileEntry, ulong>(
            0,
            (sum, file) => checked(sum + (ulong)file.Size));

        var recoveryFiles = files
            .Select(file => new RecoveryFile(
                file.SourcePath,
                file.RelativePath,
                file.Size,
                file.ModifiedUnixNanoseconds))
            .ToArray();
        var preverifiedSkips = CreateEmptySkipMasks(files.Count, destinationRoots.Length);
        var stateLeases = new List<DestinationStateLease>(destinationRoots.Length);
        try
        {
            foreach (var root in destinationRoots)
                stateLeases.Add(DestinationStateLease.Acquire(root));

            for (var slot = 0; slot < destinationRoots.Length; slot++)
            {
                var root = destinationRoots[slot];
                PreflightSafety.ValidateDestinationLayout(root, directories, scan.Files);
                var completed = RecoveryManager.PrepareAndNormalize(sourceRoot, root, recoveryFiles);
                var skippedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
                {
                    if (!completed.Contains(RecoveryManager.StateKey(recoveryFiles[fileIndex])))
                        continue;
                    preverifiedSkips[fileIndex][slot] = true;
                    skippedPaths.Add(files[fileIndex].RelativePath);
                }
                PreflightSafety.EnsureFreeSpace(root, scan.Files, skippedPaths);
                foreach (var relative in directories)
                    EnsureDestinationDirectory(root, relative);
            }

            return new PreparedCopy(
                sourceRoot,
                destinationRoots,
                files,
                directories,
                totalBytes,
                preverifiedSkips,
                sourceIsDirectory ? scan : null,
                sourceDevice,
                destinationDevices,
                stateLeases.ToArray());
        }
        catch
        {
            foreach (var lease in stateLeases)
                lease.Dispose();
            throw;
        }
    }

    private static async Task RunAsync(
        PreparedCopy copy,
        DestinationProgress[] progress,
        CopyOptions options,
        CopyJob job)
    {
        var token = job.Token;
        var expectedHashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        DestinationWorker[] workers = [];
        using var resources = new ResourceGovernor();
        var bufferBudget = AdaptiveByteBudget.CreateForSystem();
        var controlBudget = AdaptiveControlByteBudget.CreateForSystem();
        var pipeline = new PipelineGovernor(bufferBudget, Math.Max(1, Environment.SystemPageSize));
        job.Telemetry.AttachPipelineGovernor(pipeline.Snapshot);
        using var deviceSchedulers = DeviceSchedulerMap.Create(copy.SourceDevice, copy.DestinationDevices);
        job.Telemetry.AttachDeviceSchedulers(deviceSchedulers.Schedulers);
        try
        {
            var skipMasks = options.SkipSame
                ? await BuildVerifiedSkipMasksAsync(copy, progress, job, token, resources).ConfigureAwait(false)
                : CreateEmptySkipMasks(copy.Files.Count, copy.DestinationRoots.Length);
            for (var fileIndex = 0; fileIndex < copy.Files.Count; fileIndex++)
            {
                for (var slot = 0; slot < copy.DestinationRoots.Length; slot++)
                    skipMasks[fileIndex][slot] |= copy.PreverifiedSkips[fileIndex][slot];
            }

            var replayDirectory = BranchReplayPlacement.ResolveSafeDirectory(copy.SourceDevice, copy.DestinationDevices);
            workers = copy.DestinationRoots
                .Select((root, index) => new DestinationWorker(
                    root,
                    index,
                    progress[index],
                    copy.DestinationDevices[index],
                    deviceSchedulers.For(copy.DestinationDevices[index]),
                    controlBudget,
                    replayDirectory))
                .ToArray();

            var copyPhaseStarted = Stopwatch.GetTimestamp();
            var writerTasks = workers
                .Select(worker => WriterLoopAsync(worker, options, job))
                .ToArray();

            Exception? producerError = null;
            try
            {
                await ProducerLoopAsync(
                    copy,
                    workers,
                    progress,
                    skipMasks,
                    expectedHashes,
                    job,
                    pipeline,
                    bufferBudget,
                    deviceSchedulers.SharedSourceScheduler).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                producerError = ex;
            }
            finally
            {
                foreach (var worker in workers)
                    worker.Channel.Writer.TryComplete(producerError);
            }

            Exception? writerError = null;
            try
            {
                await Task.WhenAll(writerTasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                writerError = ex;
            }

            job.Telemetry.RecordCopyPhase(Stopwatch.GetElapsedTime(copyPhaseStarted));

            if (producerError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producerError).Throw();
            if (writerError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writerError).Throw();

            if (options.Verify && !token.IsCancellationRequested)
            {
                var verifyPhaseStarted = Stopwatch.GetTimestamp();
                try
                {
                    await VerifyDestinationsAsync(copy, workers, progress, job).ConfigureAwait(false);
                }
                finally
                {
                    job.Telemetry.RecordVerifyPhase(Stopwatch.GetElapsedTime(verifyPhaseStarted));
                }
            }

            for (var i = 0; i < progress.Length; i++)
            {
                if (token.IsCancellationRequested)
                    progress[i].SetPhase(DestinationPhase.Cancelled, "Cancelado");
                else if (progress[i].Snapshot().Phase is not DestinationPhase.Failed)
                    progress[i].SetPhase(DestinationPhase.Done);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var item in progress)
                item.SetPhase(DestinationPhase.Cancelled, "Cancelado");
        }
        catch (Exception ex)
        {
            foreach (var item in progress.Where(p => p.Snapshot().Phase is not DestinationPhase.Failed))
                item.SetPhase(DestinationPhase.Failed, ex.Message);
        }
        finally
        {
            foreach (var worker in workers)
            {
                worker.Channel.Writer.TryComplete();
                DrainAndRelease(worker);
                worker.Dispose();
            }
            copy.ReleaseStateLeases();
        }
    }

    private static async Task ProducerLoopAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        bool[][] skipMasks,
        Dictionary<string, byte[]> expectedHashes,
        CopyJob job,
        PipelineGovernor pipeline,
        AdaptiveByteBudget bufferBudget,
        DeviceScheduler? sharedSourceScheduler)
    {
        var token = job.Token;
        try
        {
            for (var fileIndex = 0; fileIndex < copy.Files.Count; fileIndex++)
            {
                token.ThrowIfCancellationRequested();
                await job.WaitIfPausedAsync(token).ConfigureAwait(false);
                var entry = copy.Files[fileIndex];
                ValidateSourceSnapshot(entry);

                var active = new List<DestinationWorker>();
                for (var slot = 0; slot < workers.Length; slot++)
                {
                    if (skipMasks[fileIndex][slot])
                    {
                        progress[slot].MarkSkipped((ulong)entry.Size);
                        continue;
                    }
                    if (workers[slot].IsActive) active.Add(workers[slot]);
                }
                if (active.Count == 0) continue;

                await DeliverAsync(active, new BeginMessage(entry), job).ConfigureAwait(false);

                var transferAlignment = TransferAlignmentFor(copy.SourceDevice, active);
                var signals = active
                    .Select(worker => worker.DeviceScheduler.Snapshot())
                    .Select(snapshot => new TransferDeviceSignal(
                        snapshot.CurrentQueueDepth,
                        snapshot.BestObservedQueueDepth,
                        snapshot.BestObservedThroughputBytesPerSecond,
                        snapshot.BestObservedAverageLatencyMilliseconds))
                    .ToArray();
                var readBufferSize = AdaptiveTransferSizer.Select(
                    entry.Size,
                    active.Count,
                    transferAlignment,
                    bufferBudget.TargetBytes,
                    bufferBudget.UsedBytes,
                    Math.Max(1, pipeline.Snapshot().CurrentPrefetchLimit),
                    signals);
                pipeline.SetBytesPerBlock(readBufferSize);
                job.Telemetry.RecordTransferSize(readBufferSize);

                var sourceResult = entry.Size > readBufferSize
                    ? await ReadAndFanOutPrefetchedAsync(
                        entry,
                        copy.SourceDevice,
                        active,
                        readBufferSize,
                        transferAlignment,
                        bufferBudget,
                        job,
                        pipeline,
                        sharedSourceScheduler).ConfigureAwait(false)
                    : await ReadAndFanOutSequentialAsync(
                        entry,
                        copy.SourceDevice,
                        active,
                        readBufferSize,
                        transferAlignment,
                        bufferBudget,
                        job,
                        pipeline,
                        sharedSourceScheduler).ConfigureAwait(false);
                if (sourceResult is null)
                    continue;

                var hash = sourceResult.Hash;
                var key = PathKey(entry.RelativePath);
                if (expectedHashes.TryGetValue(key, out var preflightHash) && !hash.AsSpan().SequenceEqual(preflightHash))
                    throw new IOException($"El origen cambió durante la copia: {entry.RelativePath}");
                expectedHashes[key] = hash;
                active.RemoveAll(worker => !worker.IsActive);
                await DeliverAsync(active, new EndMessage(hash), job).ConfigureAwait(false);
            }

            if (copy.SourceScan is not null)
                PreflightSafety.ValidateSourceTreeSnapshot(copy.SourceRoot, copy.SourceScan);
        }
        finally
        {
            // SharedBlock instances can outlive the producer while destination writers
            // drain their channels. Disposing the semaphore here races with the
            // final SharedBlock.Release() calls and can abort otherwise valid copies.
            // The semaphore is intentionally left for GC once the last shared block and
            // this producer scope release their references.
        }
    }

    private static async Task<SourceReadResult?> ReadAndFanOutSequentialAsync(
        FileEntry entry,
        StorageDeviceInfo sourceDevice,
        List<DestinationWorker> active,
        int readBufferSize,
        int transferAlignment,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler)
    {
        using var hasher = Hasher.New();
        DirectIoSourceReader.OverlappedSession? direct = null;
        FileStream? buffered = null;
        try
        {
            if (!DirectIoSourceReader.TryOpenOverlapped(entry.SourcePath, sourceDevice, readBufferSize, out direct))
                buffered = OpenSourceStream(entry.SourcePath);

            long totalRead = 0;
            while (totalRead < entry.Size)
            {
                job.Token.ThrowIfCancellationRequested();
                await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
                var budgetStarted = Stopwatch.GetTimestamp();
                await bufferBudget.AcquireAsync(readBufferSize, job.Token).ConfigureAwait(false);
                var budgetElapsed = Stopwatch.GetElapsedTime(budgetStarted);
                pipeline.RecordBudgetWait(budgetElapsed);
                job.Telemetry.RecordBufferWait(budgetElapsed);
                job.Telemetry.ObserveBuffer(bufferBudget.UsedBytes, bufferBudget.TargetBytes);

                SourceBufferLease? lease = SourceBufferLease.RentAligned(
                    readBufferSize,
                    transferAlignment);
                var budgetOwned = true;
                int read;
                try
                {
                    var remaining = checked((int)Math.Min(readBufferSize, entry.Size - totalRead));
                    var readStarted = Stopwatch.GetTimestamp();
                    DeviceScheduler.IoLease? sourceIo = null;
                    try
                    {
                        if (sharedSourceScheduler is not null)
                            sourceIo = await sharedSourceScheduler.AcquireIoAsync(remaining, job.Token).ConfigureAwait(false);

                        if (direct is not null)
                        {
                            try
                            {
                                read = await direct.ReadAsync(lease, readBufferSize, totalRead, job.Token).ConfigureAwait(false);
                                job.Telemetry.RecordDirectSourceRead(read);
                            }
                            catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                            {
                                direct.Dispose();
                                direct = null;
                                job.Telemetry.RecordDirectSourceFallback();
                                buffered = OpenSourceStream(entry.SourcePath);
                                buffered.Position = totalRead;
                                read = await buffered.ReadAsync(lease.Memory[..remaining], job.Token).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            read = await buffered!.ReadAsync(lease.Memory[..remaining], job.Token).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        sourceIo?.Dispose();
                    }
                    var readElapsed = Stopwatch.GetElapsedTime(readStarted);
                    pipeline.RecordSourceRead(readElapsed);
                    job.Telemetry.RecordSourceRead(read, readElapsed);

                    if (read == 0)
                        throw new IOException($"Lectura incompleta del origen: {entry.RelativePath}");

                    totalRead += read;
                    var hashStarted = Stopwatch.GetTimestamp();
                    hasher.UpdateWithJoin(lease.Memory.Span[..read]);
                    job.Telemetry.RecordSourceHash(read, Stopwatch.GetElapsedTime(hashStarted));
                    active.RemoveAll(worker => !worker.IsActive);
                    if (active.Count == 0)
                        return null;

                    var block = new SharedBlock(lease, read, readBufferSize, active.Count, bufferBudget);
                    lease = null;
                    budgetOwned = false;
                    var deliveryStarted = Stopwatch.GetTimestamp();
                    await DeliverAsync(active, new DataMessage(block), job).ConfigureAwait(false);
                    var deliveryElapsed = Stopwatch.GetElapsedTime(deliveryStarted);
                    pipeline.RecordDeliveryWait(deliveryElapsed);
                    job.Telemetry.RecordFanoutWait(deliveryElapsed);
                    active.RemoveAll(worker => !worker.IsActive);
                    if (active.Count == 0)
                        return null;
                }
                finally
                {
                    lease?.Dispose();
                    if (budgetOwned)
                        bufferBudget.Release(readBufferSize);
                }
            }

            ValidateCompletedSourceRead(entry, totalRead);
            return new SourceReadResult(totalRead, hasher.Finalize().AsSpan().ToArray());
        }
        finally
        {
            direct?.Dispose();
            if (buffered is not null)
                await buffered.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<SourceReadResult?> ReadAndFanOutPrefetchedAsync(
        FileEntry entry,
        StorageDeviceInfo sourceDevice,
        List<DestinationWorker> active,
        int readBufferSize,
        int transferAlignment,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler)
    {
        using var prefetchCancel = CancellationTokenSource.CreateLinkedTokenSource(job.Token);
        var sourceQueue = Channel.CreateUnbounded<SourceReadBlock>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        var readTask = PrefetchSourceAsync(
            entry,
            sourceDevice,
            readBufferSize,
            transferAlignment,
            sourceQueue.Writer,
            bufferBudget,
            job,
            pipeline,
            sharedSourceScheduler,
            prefetchCancel.Token);

        Exception? deliveryError = null;
        var stoppedEarly = false;
        try
        {
            while (true)
            {
                var consumerStarted = Stopwatch.GetTimestamp();
                if (!await sourceQueue.Reader.WaitToReadAsync(job.Token).ConfigureAwait(false))
                    break;
                pipeline.RecordConsumerWait(Stopwatch.GetElapsedTime(consumerStarted));
                if (!sourceQueue.Reader.TryRead(out var sourceBlock))
                    continue;
                pipeline.ReleasePrefetchSlot();
                active.RemoveAll(worker => !worker.IsActive);
                if (active.Count == 0)
                {
                    sourceBlock.Release();
                    stoppedEarly = true;
                    prefetchCancel.Cancel();
                    break;
                }

                var shared = sourceBlock.TransferToShared(active.Count);
                var deliveryStarted = Stopwatch.GetTimestamp();
                await DeliverAsync(active, new DataMessage(shared), job).ConfigureAwait(false);
                var deliveryElapsed = Stopwatch.GetElapsedTime(deliveryStarted);
                pipeline.RecordDeliveryWait(deliveryElapsed);
                job.Telemetry.RecordFanoutWait(deliveryElapsed);
                active.RemoveAll(worker => !worker.IsActive);
                if (active.Count == 0)
                {
                    stoppedEarly = true;
                    prefetchCancel.Cancel();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            deliveryError = ex;
            prefetchCancel.Cancel();
        }

        SourceReadResult? result = null;
        try
        {
            result = await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppedEarly || deliveryError is not null || job.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            deliveryError ??= ex;
        }
        finally
        {
            while (sourceQueue.Reader.TryRead(out var leftover))
            {
                pipeline.ReleasePrefetchSlot();
                leftover.Release();
            }
        }

        if (deliveryError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(deliveryError).Throw();
        if (stoppedEarly)
            return null;
        return result ?? throw new IOException($"La lectura anticipada terminó sin resultado: {entry.RelativePath}");
    }

    private static async Task<SourceReadResult> PrefetchSourceAsync(
        FileEntry entry,
        StorageDeviceInfo sourceDevice,
        int readBufferSize,
        int transferAlignment,
        ChannelWriter<SourceReadBlock> output,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler,
        CancellationToken token)
    {
        Exception? completionError = null;
        using var stageCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var hashQueue = Channel.CreateUnbounded<SourceReadBlock>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        var readTask = ReadSourceAheadAsync(
            entry,
            sourceDevice,
            readBufferSize,
            transferAlignment,
            hashQueue.Writer,
            bufferBudget,
            job,
            pipeline,
            sharedSourceScheduler,
            stageCancel.Token);

        try
        {
            using var hasher = Hasher.New();
            long totalHashed = 0;
            await foreach (var block in hashQueue.Reader.ReadAllAsync(stageCancel.Token).ConfigureAwait(false))
            {
                totalHashed += block.Length;
                var hashStarted = Stopwatch.GetTimestamp();
                hasher.UpdateWithJoin(block.Memory.Span);
                job.Telemetry.RecordSourceHash(block.Length, Stopwatch.GetElapsedTime(hashStarted));
                try
                {
                    await output.WriteAsync(block, stageCancel.Token).ConfigureAwait(false);
                }
                catch
                {
                    block.Release();
                    pipeline.ReleasePrefetchSlot();
                    throw;
                }
            }

            var totalRead = await readTask.ConfigureAwait(false);
            if (totalHashed != totalRead)
                throw new IOException($"La tubería de origen perdió datos en {entry.RelativePath}: leídos {totalRead}, procesados {totalHashed}.");
            ValidateCompletedSourceRead(entry, totalRead);
            return new SourceReadResult(totalRead, hasher.Finalize().AsSpan().ToArray());
        }
        catch (Exception ex)
        {
            completionError = ex;
            stageCancel.Cancel();
            try { await readTask.ConfigureAwait(false); }
            catch { }
            while (hashQueue.Reader.TryRead(out var leftover))
            {
                leftover.Release();
                pipeline.ReleasePrefetchSlot();
            }
            throw;
        }
        finally
        {
            output.TryComplete(completionError);
        }
    }

    private static async Task<long> ReadSourceAheadAsync(
        FileEntry entry,
        StorageDeviceInfo sourceDevice,
        int readBufferSize,
        int transferAlignment,
        ChannelWriter<SourceReadBlock> output,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler,
        CancellationToken token)
    {
        Exception? completionError = null;
        DirectIoSourceReader.OverlappedSession? direct = null;
        FileStream? buffered = null;
        try
        {
            if (!DirectIoSourceReader.TryOpenOverlapped(entry.SourcePath, sourceDevice, readBufferSize, out direct))
                buffered = OpenSourceStream(entry.SourcePath);

            long totalRead = 0;
            while (totalRead < entry.Size)
            {
                token.ThrowIfCancellationRequested();
                await job.WaitIfPausedAsync(token).ConfigureAwait(false);
                await pipeline.AcquirePrefetchSlotAsync(token).ConfigureAwait(false);
                var slotOwned = true;
                var budgetOwned = false;
                SourceBufferLease? lease = null;
                try
                {
                    var budgetStarted = Stopwatch.GetTimestamp();
                    await bufferBudget.AcquireAsync(readBufferSize, token).ConfigureAwait(false);
                    budgetOwned = true;
                    var budgetElapsed = Stopwatch.GetElapsedTime(budgetStarted);
                    pipeline.RecordBudgetWait(budgetElapsed);
                    job.Telemetry.RecordBufferWait(budgetElapsed);
                    job.Telemetry.ObserveBuffer(bufferBudget.UsedBytes, bufferBudget.TargetBytes);

                    // FAN-OUT payloads are always aligned, even when the source itself
                    // falls back to buffered I/O, so every capable destination can keep
                    // using Direct I/O without a whole-block staging copy.
                    lease = SourceBufferLease.RentAligned(
                        readBufferSize,
                        transferAlignment);

                    var readStarted = Stopwatch.GetTimestamp();
                    int read;
                    DeviceScheduler.IoLease? sourceIo = null;
                    try
                    {
                        if (sharedSourceScheduler is not null)
                            sourceIo = await sharedSourceScheduler.AcquireIoAsync(readBufferSize, token).ConfigureAwait(false);

                        if (direct is not null)
                        {
                            try
                            {
                                read = await direct.ReadAsync(lease, readBufferSize, totalRead, token).ConfigureAwait(false);
                                job.Telemetry.RecordDirectSourceRead(read);
                            }
                            catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                            {
                                lease.Dispose();
                                lease = null;
                                direct.Dispose();
                                direct = null;
                                job.Telemetry.RecordDirectSourceFallback();
                                buffered = OpenSourceStream(entry.SourcePath);
                                buffered.Position = totalRead;
                                lease = SourceBufferLease.RentAligned(
                                    readBufferSize,
                                    transferAlignment);
                                var remaining = checked((int)Math.Min(readBufferSize, entry.Size - totalRead));
                                read = await buffered.ReadAsync(lease.Memory[..remaining], token).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            var remaining = checked((int)Math.Min(readBufferSize, entry.Size - totalRead));
                            read = await buffered!.ReadAsync(lease.Memory[..remaining], token).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        sourceIo?.Dispose();
                    }

                    var readElapsed = Stopwatch.GetElapsedTime(readStarted);
                    pipeline.RecordSourceRead(readElapsed);
                    job.Telemetry.RecordSourceRead(read, readElapsed);
                    if (read == 0)
                    {
                        lease.Dispose();
                        lease = null;
                        bufferBudget.Release(readBufferSize);
                        budgetOwned = false;
                        pipeline.ReleasePrefetchSlot();
                        slotOwned = false;
                        throw new IOException($"Lectura incompleta del origen: {entry.RelativePath}");
                    }

                    totalRead += read;
                    var block = new SourceReadBlock(lease, read, readBufferSize, bufferBudget);
                    lease = null;
                    budgetOwned = false;
                    await output.WriteAsync(block, token).ConfigureAwait(false);
                    slotOwned = false;
                }
                catch
                {
                    if (budgetOwned)
                        bufferBudget.Release(readBufferSize);
                    if (slotOwned)
                        pipeline.ReleasePrefetchSlot();
                    throw;
                }
            }
            return totalRead;
        }
        catch (Exception ex)
        {
            completionError = ex;
            throw;
        }
        finally
        {
            direct?.Dispose();
            if (buffered is not null)
                await buffered.DisposeAsync().ConfigureAwait(false);
            output.TryComplete(completionError);
        }
    }

    private static FileStream OpenSourceStream(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1,
        });

    private static void ValidateCompletedSourceRead(FileEntry entry, long totalRead)
    {
        if (totalRead != entry.Size)
            throw new IOException($"El origen cambió de tamaño durante la copia: {entry.RelativePath}");
        ValidateSourceSnapshot(entry);
    }

    private static async Task DeliverAsync(
        IReadOnlyList<DestinationWorker> recipients,
        FanoutMessage message,
        CopyJob job)
    {
        if (recipients.Count == 0)
            return;

        if (message is DataMessage dataMessage)
        {
            await DeliverDataAsync(recipients, dataMessage, job).ConfigureAwait(false);
            return;
        }

        var control = (ControlMessage)message;
        for (var index = 0; index < recipients.Count; index++)
            await DeliverControlAsync(recipients[index], control, job).ConfigureAwait(false);
    }

    private static async Task DeliverDataAsync(
        IReadOnlyList<DestinationWorker> recipients,
        DataMessage message,
        CopyJob job)
    {
        var index = -1;
        try
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

            for (index = 0; index < recipients.Count; index++)
            {
                var worker = recipients[index];
                if (!worker.IsActive)
                {
                    message.Block.Release();
                    continue;
                }

                var backlogOwned = false;
                var pendingPayloadOwned = false;
                var queueOwned = false;
                var blockOwned = true;
                try
                {
                    worker.DeviceScheduler.ReserveBacklog(message.Block.Length);
                    backlogOwned = true;
                    worker.ReservePendingPayload(message.Block.Length);
                    pendingPayloadOwned = true;

                    if (!worker.IsActive)
                    {
                        worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                        backlogOwned = false;
                        worker.ReleasePendingPayload(message.Block.Length);
                        pendingPayloadOwned = false;
                        message.Block.Release();
                        blockOwned = false;
                        continue;
                    }

                    FanoutMessage branchMessage = message;
                    var shouldReplay = recipients.Count > 1 &&
                        worker.ReplayStore.IsEnabled &&
                        worker.ReplayGate.ShouldReplay(
                            worker.PendingPayloadBytes,
                            worker.DeviceScheduler.BacklogTargetBytes,
                            Stopwatch.GetTimestamp());
                    if (shouldReplay)
                    {
                        try
                        {
                            var replayStarted = Stopwatch.GetTimestamp();
                            var segment = await worker.ReplayStore.SpillAsync(
                                message.Block.Memory,
                                message.Block.VerificationCrc32C,
                                job.Token).ConfigureAwait(false);
                            job.Telemetry.RecordBranchReplayWrite(
                                segment.Length,
                                Stopwatch.GetElapsedTime(replayStarted));
                            message.Block.Release();
                            blockOwned = false;
                            branchMessage = new ReplayDataMessage(segment);
                        }
                        catch (IOException)
                        {
                            // Replay is an optimization. If the temporary volume cannot
                            // accept it, preserve correctness by keeping the shared block.
                        }
                    }

                    worker.IncrementQueueDepth();
                    queueOwned = true;
                    if (worker.Channel.Writer.TryWrite(branchMessage))
                    {
                        queueOwned = false;
                        backlogOwned = false;
                        pendingPayloadOwned = false;
                        blockOwned = false;
                        continue;
                    }

                    worker.DecrementQueueDepth();
                    queueOwned = false;
                    worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                    backlogOwned = false;
                    worker.ReleasePendingPayload(message.Block.Length);
                    pendingPayloadOwned = false;
                    message.Block.Release();
                    blockOwned = false;
                    if (worker.IsActive)
                        worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
                }
                catch
                {
                    if (queueOwned)
                        worker.DecrementQueueDepth();
                    if (backlogOwned)
                        worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                    if (pendingPayloadOwned)
                        worker.ReleasePendingPayload(message.Block.Length);
                    if (blockOwned)
                        message.Block.Release();
                    throw;
                }
            }
        }
        catch
        {
            for (var remaining = index + 1; remaining < recipients.Count; remaining++)
                message.Block.Release();
            throw;
        }
    }

    private static async ValueTask DeliverControlAsync(
        DestinationWorker worker,
        ControlMessage message,
        CopyJob job)
    {
        if (!worker.IsActive)
            return;

        job.Token.ThrowIfCancellationRequested();
        await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
        if (!worker.IsActive)
            return;

        var reservationBytes = AdaptiveControlByteBudget.EstimatedDeliveryBytes;
        await worker.ControlBudget.AcquireAsync(reservationBytes, job.Token).ConfigureAwait(false);
        var delivery = new ControlDelivery(message, worker.ControlBudget, reservationBytes);
        var queueOwned = false;
        try
        {
            if (!worker.IsActive)
                return;

            worker.IncrementQueueDepth();
            queueOwned = true;
            if (worker.Channel.Writer.TryWrite(delivery))
            {
                delivery = null;
                queueOwned = false;
                return;
            }

            worker.DecrementQueueDepth();
            queueOwned = false;
            if (worker.IsActive)
                worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
        }
        finally
        {
            if (queueOwned)
                worker.DecrementQueueDepth();
            delivery?.ReleaseBudget();
        }
    }

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
                var replay = effectiveMessage as ReplayDataMessage;
                var dataOwnedByWriter = data is not null;
                var replayOwnedByWriter = replay is not null;
                if (data is not null)
                    worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
                else if (replay is not null)
                    worker.DeviceScheduler.ReleaseBacklog(replay.Segment.Length);
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
                                new VerificationBlock(chunkData.Block.Length, chunkData.Block.VerificationCrc32C));
                            current.PendingWrites.Add(
                                WriteBlockAtOffsetAsync(worker, current, chunkData.Block, offset, job));
                            dataOwnedByWriter = false;
                            PruneCompletedSuccesses(current);
                            break;

                        case ReplayDataMessage replayData when current is not null:
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

                            var replayOffset = current.ReserveWriteOffset(replayData.Segment.Length);
                            current.VerificationBlocks.Add(
                                new VerificationBlock(replayData.Segment.Length, replayData.Segment.VerificationCrc32C));
                            current.PendingWrites.Add(
                                WriteReplayBlockAtOffsetAsync(worker, current, replayData.Segment, replayOffset, job));
                            replayOwnedByWriter = false;
                            PruneCompletedSuccesses(current);
                            break;

                        case DataMessage:
                        case ReplayDataMessage:
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
                    if (dataOwnedByWriter && data is not null)
                        ReleaseBranchBlock(worker, data.Block);
                    if (replayOwnedByWriter && replay is not null)
                        worker.ReleasePendingPayload(replay.Segment.Length);
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
                await ReleasePendingWritesAsync(worker, current).ConfigureAwait(false);
                current.Stream?.Dispose();
                current.DirectSession?.Dispose();
                TryDelete(current.PartPath);
                if (current.Copied > 0 && !current.Failed)
                    worker.Progress.RollbackWritten((ulong)current.Copied);
            }
            DrainAndRelease(worker);
        }
    }

    private static CurrentFile BeginFile(DestinationWorker worker, FileEntry entry)
    {
        worker.Progress.SetLastFile(entry.RelativePath);
        ValidateRuntimeDestinationPath(worker.Root, entry.RelativePath);
        StateLayout.PrepareTempDirectory(worker.Root);
        var destination = Path.Combine(worker.Root, entry.RelativePath);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new IOException($"Destino inválido: {destination}");
        Directory.CreateDirectory(parent);
        WindowsPath.EnsureNormalDirectory(parent, "La carpeta de destino");
        var transient = StateLayout.TransientPaths(worker.Root, destination);
        var part = transient.PartPath;
        TryDelete(part);
        var directRequested = DirectIoDestinationWriter.IsEligible(worker.Device, entry.Size);
        var preallocationSize = StoragePreallocationPolicy.GetPreallocationSize(part, entry.Size);
        FileStream? stream = null;
        DirectIoDestinationWriter.Session? directSession = null;
        if (directRequested)
        {
            using (OpenPartStream(part, FileMode.CreateNew, preallocationSize)) { }
            if (!DirectIoDestinationWriter.TryOpen(part, worker.Device, entry.Size, out directSession))
                stream = ReopenPart(part);
        }
        else
        {
            stream = OpenPartStream(part, FileMode.CreateNew, preallocationSize);
        }
        return new CurrentFile(entry, destination, part, transient.BackupPath, stream, directSession, directRequested);
    }

    private static async Task<PendingWriteResult> WriteReplayBlockAtOffsetAsync(
        DestinationWorker worker,
        CurrentFile current,
        BranchReplayStore.Segment segment,
        long offset,
        CopyJob job)
    {
        SourceBufferLease? lease = SourceBufferLease.RentAligned(
            segment.Length,
            BufferAlignmentFor(worker.Device));
        try
        {
            var replayStarted = Stopwatch.GetTimestamp();
            await worker.ReplayStore.ReadAsync(
                segment,
                lease.Memory[..segment.Length],
                job.Token).ConfigureAwait(false);
            job.Telemetry.RecordBranchReplayRead(
                segment.Length,
                Stopwatch.GetElapsedTime(replayStarted));
            var block = new SharedBlock(lease, segment.Length, segment.VerificationCrc32C);
            lease = null;
            return await WriteBlockAtOffsetAsync(worker, current, block, offset, job).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lease?.Dispose();
            worker.ReleasePendingPayload(segment.Length);
            return PendingWriteResult.Failed(ex);
        }
    }
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

                var started = Stopwatch.GetTimestamp();
                int operations;
                try
                {
                    operations = await direct.WriteAsync(
                        data,
                        offset,
                        current.Entry.Size,
                        payloadIsAligned: true,
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
                ReleaseBranchBlock(worker, block);
                return PendingWriteResult.Success();
            }
            catch (Exception ex)
            {
                ReleaseBranchBlock(worker, block);
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
                var started = Stopwatch.GetTimestamp();
                var operations = await DestinationWriteCoordinator.WriteAsync(
                    stream.SafeFileHandle,
                    data,
                    offset,
                    worker.DeviceScheduler,
                    job.Token).ConfigureAwait(false);

                for (var operation = 0; operation < operations; operation++)
                    job.Telemetry.RecordWriteOperation();
                job.Telemetry.RecordWrite(data.Length, Stopwatch.GetElapsedTime(started));
                current.RecordCompletedWrite(data.Length);
                worker.Progress.AddWritten(data.Length);
                worker.NoteProgress();
                ReleaseBranchBlock(worker, block);
                return PendingWriteResult.Success();
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt >= Retries || !TransientIoErrorClassifier.IsTransient(ex))
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

        ReleaseBranchBlock(worker, block);
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
                ReleaseRetryBlock(worker, retry.RetryBlock);
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
                ReleaseRetryBlock(worker, retry.RetryBlock);
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

    private static async Task ReleasePendingWritesAsync(DestinationWorker worker, CurrentFile current)
    {
        if (current.PendingWrites.Count == 0)
            return;
        var pending = current.PendingWrites.ToArray();
        current.PendingWrites.Clear();
        var results = await Task.WhenAll(pending).ConfigureAwait(false);
        foreach (var result in results)
            ReleaseRetryBlock(worker, result.RetryBlock);
    }

    private static void ReleaseRetryBlock(DestinationWorker worker, SharedBlock? block)
    {
        if (block is not null)
            ReleaseBranchBlock(worker, block);
    }

    private static void ReleaseBranchBlock(DestinationWorker worker, SharedBlock block)
    {
        worker.ReleasePendingPayload(block.Length);
        block.Release();
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
        DestinationWorker worker,
        CurrentFile current,
        byte[] expectedHash,
        CopyOptions options,
        RecoveryCheckpointWriter recovery,
        CopyJob job)
    {
        if (current.Failed) return;
        if (current.ScheduledBytes != current.Entry.Size || current.Copied != current.Entry.Size)
        {
            current.Failed = true;
            current.Stream?.Dispose();
            current.Stream = null;
            current.DirectSession?.Dispose();
            current.DirectSession = null;
            TryDelete(current.PartPath);
            worker.Progress.RollbackWritten((ulong)current.Copied);
            var error = $"Tamaño inesperado en {current.Entry.RelativePath}";
            if (options.KeepGoing) worker.Progress.MarkError(error);
            else worker.Fail(error);
            return;
        }

        if (current.DirectSession is not null)
        {
            var flushStarted = Stopwatch.GetTimestamp();
            current.DirectSession.FinalizeLength(current.Entry.Size);
            current.DirectSession.FlushToDisk();
            job.Telemetry.RecordFlush(Stopwatch.GetElapsedTime(flushStarted));
            current.DirectSession.Dispose();
            current.DirectSession = null;
        }
        else if (current.Stream is not null)
        {
            var flushStarted = Stopwatch.GetTimestamp();
            current.Stream.Flush(flushToDisk: true);
            job.Telemetry.RecordFlush(Stopwatch.GetElapsedTime(flushStarted));
            current.Stream.Dispose();
            current.Stream = null;
        }

        var actualSize = new FileInfo(current.PartPath).Length;
        if (actualSize != current.Entry.Size)
            throw new IOException($"Tamaño físico incorrecto en {current.PartPath}: esperado {current.Entry.Size}, obtenido {actualSize}.");

        ValidateRuntimeDestinationPath(worker.Root, current.Entry.RelativePath);
        var commitStarted = Stopwatch.GetTimestamp();
        AtomicFileCommit.Commit(current.PartPath, current.DestinationPath, current.BackupPath);
        job.Telemetry.RecordCommit(Stopwatch.GetElapsedTime(commitStarted));
        File.SetLastWriteTimeUtc(current.DestinationPath, current.Entry.LastWriteTimeUtc);
        var recoveryStarted = Stopwatch.GetTimestamp();
        recovery.Append(
            new RecoveryFile(
                current.Entry.SourcePath,
                current.Entry.RelativePath,
                current.Entry.Size,
                current.Entry.ModifiedUnixNanoseconds),
            expectedHash);
        job.Telemetry.RecordRecovery(Stopwatch.GetElapsedTime(recoveryStarted));
        worker.VerificationPlans[PathKey(current.Entry.RelativePath)] =
            new VerificationPlan(current.Entry.Size, current.VerificationBlocks.ToArray());
        worker.Progress.MarkDone();
    }

    private static async Task VerifyDestinationsAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        CopyJob job)
    {
        var readBudget = VerificationReadBudget.CreateForSystem();
        var activeSlots = Enumerable.Range(0, workers.Length)
            .Where(slot => workers[slot].IsActive)
            .ToArray();
        var tasks = activeSlots.Select(async slot =>
        {
            var verifyEntries = copy.Files
                .Where(entry => workers[slot].VerificationPlans.ContainsKey(PathKey(entry.RelativePath)))
                .ToArray();
            var verifyBytes = verifyEntries.Aggregate<FileEntry, ulong>(
                0,
                (sum, entry) => checked(sum + (ulong)entry.Size));
            progress[slot].SetVerifyWork(verifyBytes, (ulong)verifyEntries.Length);
            if (verifyEntries.Length == 0)
                return;

            progress[slot].SetPhase(DestinationPhase.Verifying);
            foreach (var entry in verifyEntries)
            {
                job.Token.ThrowIfCancellationRequested();
                await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
                var key = PathKey(entry.RelativePath);
                if (!workers[slot].VerificationPlans.TryGetValue(key, out var plan))
                    continue;
                progress[slot].SetLastFile(entry.RelativePath);
                var destination = Path.Combine(workers[slot].Root, entry.RelativePath);
                if (!File.Exists(destination))
                {
                    workers[slot].Fail($"Falta el archivo durante verificación: {destination}");
                    break;
                }
                ValidateRuntimeDestinationPath(workers[slot].Root, entry.RelativePath);
                WindowsPath.EnsureRegularFile(destination, "El archivo durante verificación");
                if (new FileInfo(destination).Length != entry.Size)
                {
                    workers[slot].Fail($"Tamaño no coincide durante verificación: {destination}");
                    break;
                }

                var valid = await FastVerificationReader.VerifyAsync(
                    destination,
                    copy.DestinationDevices[slot],
                    workers[slot].DeviceScheduler,
                    plan,
                    readBudget,
                    job,
                    progress[slot]).ConfigureAwait(false);
                if (!valid)
                {
                    workers[slot].Fail($"CRC32C no coincide durante verificación: {destination}");
                    break;
                }
                progress[slot].MarkVerifyFileDone();
            }
        }).ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            job.Telemetry.RecordVerificationBufferBudget(readBudget.LimitBytes, readBudget.PeakUsedBytes);
        }
    }

    private static async Task<bool[][]> BuildVerifiedSkipMasksAsync(
        PreparedCopy copy,
        DestinationProgress[] progress,
        CopyJob job,
        CancellationToken token,
        ResourceGovernor resources)
    {
        var masks = CreateEmptySkipMasks(copy.Files.Count, copy.DestinationRoots.Length);
        for (var fileIndex = 0; fileIndex < copy.Files.Count; fileIndex++)
        {
            token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(token).ConfigureAwait(false);
            var entry = copy.Files[fileIndex];
            var candidates = new List<int>();
            for (var slot = 0; slot < copy.DestinationRoots.Length; slot++)
            {
                var destination = Path.Combine(copy.DestinationRoots[slot], entry.RelativePath);
                if (!File.Exists(destination) || WindowsPath.IsReparsePoint(destination)) continue;
                var info = new FileInfo(destination);
                if (info.Length == entry.Size && ToUnixNanoseconds(info.LastWriteTimeUtc) == entry.ModifiedUnixNanoseconds)
                    candidates.Add(slot);
            }
            if (candidates.Count == 0) continue;

            ValidateSourceSnapshot(entry);
            var sourceHash = await HashFileAsync(entry.SourcePath, token, resources).ConfigureAwait(false);
            ValidateSourceSnapshot(entry);
            var checks = candidates.Select(async slot =>
            {
                var destination = Path.Combine(copy.DestinationRoots[slot], entry.RelativePath);
                ValidateRuntimeDestinationPath(copy.DestinationRoots[slot], entry.RelativePath);
                WindowsPath.EnsureRegularFile(destination, "El archivo candidato de SkipSame");
                if (new FileInfo(destination).Length != entry.Size)
                    return;
                var destinationHash = await HashFileAsync(destination, token, resources).ConfigureAwait(false);
                if (destinationHash.AsSpan().SequenceEqual(sourceHash))
                {
                    masks[fileIndex][slot] = true;
                    progress[slot].SetLastFile(entry.RelativePath);
                }
            }).ToArray();
            await Task.WhenAll(checks).ConfigureAwait(false);
        }
        return masks;
    }

    private static bool[][] CreateEmptySkipMasks(int files, int destinations) =>
        Enumerable.Range(0, files).Select(_ => new bool[destinations]).ToArray();

    private static async Task<byte[]> HashFileAsync(
        string path,
        CancellationToken token,
        ResourceGovernor resources)
    {
        using var hasher = Hasher.New();
        const int bufferSize = 4 * 1024 * 1024;
        using var buffer = SourceBufferLease.RentBuffered(bufferSize);
        await using var stream = OpenSourceStream(path);

        while (true)
        {
            var read = await stream.ReadAsync(buffer.Memory, token).ConfigureAwait(false);
            if (read == 0)
                break;

            using var lease = await resources.EnterCpuWorkAsync(token).ConfigureAwait(false);
            hasher.UpdateWithJoin(buffer.Memory.Span[..read]);
        }

        return hasher.Finalize().AsSpan().ToArray();
    }


    private static void EnsureDestinationDirectory(string root, string relative)
    {
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current))
                throw new IOException($"Componente de destino ya no es carpeta: {current}");
            Directory.CreateDirectory(current);
            WindowsPath.EnsureNormalDirectory(current, "La carpeta de destino");
        }
    }

    private static void ValidateRuntimeDestinationPath(string root, string relative)
    {
        WindowsPath.EnsureNormalDirectory(root, "El destino");
        var current = root;
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if (WindowsPath.IsReparsePoint(current))
                throw new IOException($"La ruta de destino cambió a symlink/junction/reparse point: {current}");
            if (index < parts.Length - 1 && !Directory.Exists(current))
                throw new IOException($"Componente de destino ya no es carpeta: {current}");
            if (index == parts.Length - 1 && Directory.Exists(current))
                throw new IOException($"El destino final cambió a carpeta: {current}");
        }
    }

    private static void ValidateSourceSnapshot(FileEntry entry)
    {
        WindowsPath.EnsureRegularFile(entry.SourcePath, "El origen");
        var info = new FileInfo(entry.SourcePath);
        if (info.Length != entry.Size || ToUnixNanoseconds(info.LastWriteTimeUtc) != entry.ModifiedUnixNanoseconds)
            throw new IOException($"El origen cambió: {entry.SourcePath}");
    }

    private static void RejectReparse(string path, string label)
    {
        if (WindowsPath.IsReparsePoint(path))
            throw new IOException($"{label} no puede ser symlink/junction/reparse point: {path}");
    }

    private static string PathKey(string relative) => relative.Replace('/', '\\');

    private static long ToUnixNanoseconds(DateTime utc) =>
        checked((utc.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks) * 100L);
    private static int BufferAlignmentFor(StorageDeviceInfo device)
    {
        var alignment = Math.Max(1, Environment.SystemPageSize);
        if (!device.HasKnownSectorAlignment)
            return alignment;
        var required = DirectIoSourceReader.RequiredAlignment(device);
        return required > 0 && (required & (required - 1)) == 0
            ? Math.Max(alignment, required)
            : alignment;
    }
    private static int TransferAlignmentFor(
        StorageDeviceInfo source,
        IReadOnlyList<DestinationWorker> active)
    {
        var alignment = Math.Max(1, Environment.SystemPageSize);
        if (source.HasKnownSectorAlignment)
        {
            var sourceAlignment = DirectIoSourceReader.RequiredAlignment(source);
            if (sourceAlignment > 0 && (sourceAlignment & (sourceAlignment - 1)) == 0)
                alignment = Math.Max(alignment, sourceAlignment);
        }
        foreach (var worker in active)
        {
            if (!worker.Device.HasKnownSectorAlignment)
                continue;
            var destinationAlignment = DirectIoSourceReader.RequiredAlignment(worker.Device);
            if (destinationAlignment > 0 && (destinationAlignment & (destinationAlignment - 1)) == 0)
                alignment = Math.Max(alignment, destinationAlignment);
        }
        return alignment;
    }


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

    private static void DrainAndRelease(DestinationWorker worker)
    {
        while (worker.Channel.Reader.TryRead(out var message))
        {
            worker.DecrementQueueDepth();
            if (message is DataMessage data)
            {
                worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
                ReleaseBranchBlock(worker, data.Block);
            }
            else if (message is ReplayDataMessage replay)
            {
                worker.DeviceScheduler.ReleaseBacklog(replay.Segment.Length);
                worker.ReleasePendingPayload(replay.Segment.Length);
            }
            else
            {
                ReleaseQueuedControl(message);
            }
        }
    }

    private static void ReleaseQueuedControl(FanoutMessage message)
    {
        if (message is ControlDelivery control)
            control.ReleaseBudget();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private sealed record PreparedCopy(
        string SourceRoot,
        string[] DestinationRoots,
        IReadOnlyList<FileEntry> Files,
        IReadOnlyList<string> Directories,
        ulong TotalBytes,
        bool[][] PreverifiedSkips,
        SourceTreeScan? SourceScan,
        StorageDeviceInfo SourceDevice,
        StorageDeviceInfo[] DestinationDevices,
        DestinationStateLease[] StateLeases)
    {
        internal void ReleaseStateLeases()
        {
            foreach (var lease in StateLeases)
                lease.Dispose();
        }
    }

    private sealed record FileEntry(
        string SourcePath,
        string RelativePath,
        long Size,
        DateTime LastWriteTimeUtc,
        long ModifiedUnixNanoseconds);

    private sealed record SourceReadResult(long BytesRead, byte[] Hash);

    private sealed class SourceReadBlock
    {
        private SourceBufferLease? _buffer;
        private readonly int _reservedBytes;
        private readonly AdaptiveByteBudget _budget;

        public SourceReadBlock(SourceBufferLease buffer, int length, int reservedBytes, AdaptiveByteBudget budget)
        {
            _buffer = buffer;
            Length = length;
            _reservedBytes = reservedBytes;
            _budget = budget;
        }

        public int Length { get; }
        public ReadOnlyMemory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(SourceReadBlock))).Memory[..Length];

        public SharedBlock TransferToShared(int references)
        {
            if (references <= 0)
                throw new ArgumentOutOfRangeException(nameof(references));
            var buffer = Interlocked.Exchange(ref _buffer, null)
                ?? throw new ObjectDisposedException(nameof(SourceReadBlock));
            return new SharedBlock(buffer, Length, _reservedBytes, references, _budget);
        }

        public void Release()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is null)
                return;
            buffer.Dispose();
            if (_budget is not null && _reservedBytes > 0)
                _budget.Release(_reservedBytes);
        }
    }

    private abstract record FanoutMessage;
    private abstract record ControlMessage : FanoutMessage;
    private sealed record BeginMessage(FileEntry Entry) : ControlMessage;
    private sealed record DataMessage(SharedBlock Block) : FanoutMessage;
    private sealed record ReplayDataMessage(BranchReplayStore.Segment Segment) : FanoutMessage;
    private sealed record EndMessage(byte[] Hash) : ControlMessage;

    private sealed record ControlDelivery : FanoutMessage
    {
        private AdaptiveControlByteBudget? _budget;
        private readonly int _reservedBytes;

        internal ControlDelivery(
            ControlMessage message,
            AdaptiveControlByteBudget budget,
            int reservedBytes)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            _budget = budget ?? throw new ArgumentNullException(nameof(budget));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reservedBytes);
            _reservedBytes = reservedBytes;
        }

        internal ControlMessage Message { get; }

        internal void ReleaseBudget()
        {
            var budget = Interlocked.Exchange(ref _budget, null);
            budget?.Release(_reservedBytes);
        }
    }

    internal sealed class SharedBlock
    {
        private SourceBufferLease? _buffer;
        private int _references;
        private readonly int _reservedBytes;
        private readonly AdaptiveByteBudget? _budget;

        public SharedBlock(byte[] buffer, int length, int reservedBytes, int references, AdaptiveByteBudget budget)
            : this(SourceBufferLease.OwnPooled(buffer, reservedBytes), length, reservedBytes, references, budget)
        {
        }

        internal SharedBlock(SourceBufferLease buffer, int length, int reservedBytes, int references, AdaptiveByteBudget budget)
        {
            _buffer = buffer;
            Length = length;
            VerificationCrc32C = FastCrc32C.Compute(buffer.Memory.Span[..length]);
            _reservedBytes = reservedBytes;
            _references = references;
            _budget = budget;
        }

        internal SharedBlock(SourceBufferLease buffer, int length, uint verificationCrc32)
        {
            _buffer = buffer;
            Length = length;
            VerificationCrc32C = verificationCrc32;
            _reservedBytes = 0;
            _references = 1;
            _budget = null;
        }

        public int Length { get; }
        public uint VerificationCrc32C { get; }
        public ReadOnlyMemory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(SharedBlock))).Memory[..Length];
        internal bool IsAlignedFor(int alignment) =>
            (_buffer ?? throw new ObjectDisposedException(nameof(SharedBlock))).IsAlignedFor(alignment);

        public void Release()
        {
            var remaining = Interlocked.Decrement(ref _references);
            if (remaining > 0)
                return;
            if (remaining < 0)
                throw new InvalidOperationException("SharedBlock liberado más veces que referencias asignadas.");
            var buffer = Interlocked.Exchange(ref _buffer, null)
                ?? throw new InvalidOperationException("SharedBlock perdió su buffer antes de la última liberación.");
            buffer.Dispose();
            if (_budget is not null && _reservedBytes > 0)
                _budget.Release(_reservedBytes);
        }
    }

    internal sealed class PipelineGovernor
    {
        private const int MinPrefetch = 1;
        private const int InitialPrefetch = 4;
        private const int SamplesPerDecision = 8;

        private readonly object _gate = new();
        private readonly Queue<PrefetchWaiter> _slotWaiters = new();
        private readonly AdaptiveByteBudget _budget;
        private int _bytesPerBlock;
        private int _prefetchLimit;
        private int _minimumObservedPrefetchLimit;
        private int _maximumObservedPrefetchLimit;
        private int _inFlight;
        private int _samples;
        private int _decisionCount;
        private int _upshifts;
        private int _downshifts;
        private string _lastDecision = "initial";
        private double _consumerWaitMs;
        private double _deliveryWaitMs;
        private double _budgetWaitMs;
        private double _readMs;
        private double _totalConsumerWaitMs;
        private double _totalDeliveryWaitMs;
        private double _totalBudgetWaitMs;
        private double _totalReadMs;

        internal PipelineGovernor(AdaptiveByteBudget budget, int bytesPerBlock)
        {
            _budget = budget ?? throw new ArgumentNullException(nameof(budget));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerBlock);
            _bytesPerBlock = bytesPerBlock;
            _prefetchLimit = Math.Min(InitialPrefetch, CurrentCapacityLocked());
            _minimumObservedPrefetchLimit = _prefetchLimit;
            _maximumObservedPrefetchLimit = _prefetchLimit;
        }

        internal int InFlight
        {
            get { lock (_gate) return _inFlight; }
        }

        internal PipelineGovernorSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new PipelineGovernorSnapshot(
                    _prefetchLimit,
                    _minimumObservedPrefetchLimit,
                    _maximumObservedPrefetchLimit,
                    _inFlight,
                    _decisionCount,
                    _upshifts,
                    _downshifts,
                    _lastDecision,
                    TimeSpan.FromMilliseconds(_totalConsumerWaitMs),
                    TimeSpan.FromMilliseconds(_totalDeliveryWaitMs),
                    TimeSpan.FromMilliseconds(_totalBudgetWaitMs),
                    TimeSpan.FromMilliseconds(_totalReadMs));
            }
        }

        internal void SetBytesPerBlock(int bytesPerBlock)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerBlock);
            lock (_gate)
            {
                _bytesPerBlock = bytesPerBlock;
                var capacity = CurrentCapacityLocked();
                if (_prefetchLimit > capacity)
                    _prefetchLimit = capacity;
                PumpSlotsLocked();
            }
        }
        public ValueTask AcquirePrefetchSlotAsync(CancellationToken token)
        {
            lock (_gate)
            {
                var capacity = CurrentCapacityLocked();
                if (_prefetchLimit > capacity)
                    _prefetchLimit = capacity;
                if (_inFlight < _prefetchLimit && _slotWaiters.Count == 0)
                {
                    _inFlight++;
                    return ValueTask.CompletedTask;
                }

                var waiter = new PrefetchWaiter();
                _slotWaiters.Enqueue(waiter);
                return new ValueTask(WaitForPrefetchSlotAsync(waiter, token));
            }
        }

        private async Task WaitForPrefetchSlotAsync(PrefetchWaiter waiter, CancellationToken token)
        {
            try
            {
                await waiter.Ready.Task.WaitAsync(token).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate)
                {
                    if (waiter.Granted)
                    {
                        waiter.Granted = false;
                        if (_inFlight <= 0)
                            throw new InvalidOperationException("Contabilidad de prefetch inválida durante cancelación.");
                        _inFlight--;
                    }
                    else
                    {
                        waiter.Cancelled = true;
                    }
                    PumpSlotsLocked();
                }
                throw;
            }
        }

        public void ReleasePrefetchSlot()
        {
            lock (_gate)
            {
                if (_inFlight <= 0)
                    throw new InvalidOperationException("Se intentó liberar un slot de prefetch no adquirido.");
                _inFlight--;
                EvaluateLocked();
                PumpSlotsLocked();
            }
        }

        public void RecordConsumerWait(TimeSpan elapsed) => RecordSample(elapsed.TotalMilliseconds, SampleKind.Consumer);
        public void RecordDeliveryWait(TimeSpan elapsed) => RecordSample(elapsed.TotalMilliseconds, SampleKind.Delivery);
        public void RecordBudgetWait(TimeSpan elapsed) => RecordSample(elapsed.TotalMilliseconds, SampleKind.Budget);
        public void RecordSourceRead(TimeSpan elapsed) => RecordSample(elapsed.TotalMilliseconds, SampleKind.Read);

        private void RecordSample(double milliseconds, SampleKind kind)
        {
            lock (_gate)
            {
                switch (kind)
                {
                    case SampleKind.Consumer:
                        _consumerWaitMs += milliseconds;
                        _totalConsumerWaitMs += milliseconds;
                        break;
                    case SampleKind.Delivery:
                        _deliveryWaitMs += milliseconds;
                        _totalDeliveryWaitMs += milliseconds;
                        break;
                    case SampleKind.Budget:
                        _budgetWaitMs += milliseconds;
                        _totalBudgetWaitMs += milliseconds;
                        break;
                    case SampleKind.Read:
                        _readMs += milliseconds;
                        _totalReadMs += milliseconds;
                        break;
                }
                if (kind == SampleKind.Consumer)
                    _samples++;
                EvaluateLocked();
            }
        }

        private void EvaluateLocked()
        {
            if (_samples < SamplesPerDecision)
                return;

            var starvation = _consumerWaitMs;
            var pressure = _deliveryWaitMs + _budgetWaitMs;
            var sourceCost = _readMs;
            var previous = _prefetchLimit;
            var capacity = CurrentCapacityLocked();
            var decision = "hold:balanced";

            if (pressure > starvation * 1.5 && pressure > sourceCost)
            {
                _prefetchLimit = Math.Max(MinPrefetch, _prefetchLimit / 2);
                decision = _prefetchLimit < previous ? "decrease:pressure" : "hold:min";
            }
            else if (starvation > pressure * 1.5 && starvation > sourceCost * 0.25)
            {
                var doubled = previous > int.MaxValue / 2 ? int.MaxValue : previous * 2;
                _prefetchLimit = Math.Min(capacity, Math.Max(previous + 1, doubled));
                decision = _prefetchLimit > previous ? "increase:starvation" : "hold:memory";
            }
            else if (_prefetchLimit > capacity)
            {
                _prefetchLimit = capacity;
                decision = "decrease:memory";
            }

            _decisionCount++;
            if (_prefetchLimit > previous)
                _upshifts++;
            else if (_prefetchLimit < previous)
                _downshifts++;
            _minimumObservedPrefetchLimit = Math.Min(_minimumObservedPrefetchLimit, _prefetchLimit);
            _maximumObservedPrefetchLimit = Math.Max(_maximumObservedPrefetchLimit, _prefetchLimit);
            _lastDecision = decision;

            _samples = 0;
            _consumerWaitMs = 0;
            _deliveryWaitMs = 0;
            _budgetWaitMs = 0;
            _readMs = 0;
            PumpSlotsLocked();
        }

        private int CurrentCapacityLocked() =>
            Math.Max(MinPrefetch, _budget.GetAdmissibleConcurrency(_bytesPerBlock));

        private void PumpSlotsLocked()
        {
            var capacity = CurrentCapacityLocked();
            if (_prefetchLimit > capacity)
                _prefetchLimit = capacity;
            while (_inFlight < _prefetchLimit && _slotWaiters.Count > 0)
            {
                var waiter = _slotWaiters.Dequeue();
                if (waiter.Cancelled)
                    continue;
                waiter.Granted = true;
                _inFlight++;
                waiter.Ready.TrySetResult();
            }
        }

        private sealed class PrefetchWaiter
        {
            public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Cancelled { get; set; }
            public bool Granted { get; set; }
        }

        private enum SampleKind
        {
            Consumer,
            Delivery,
            Budget,
            Read,
        }
    }

    internal sealed class ResourceGovernor : IDisposable
    {
        private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
        private const double CpuGrowThreshold = 0.60;
        private const double CpuShrinkThreshold = 0.85;

        private readonly object _gate = new();
        private readonly Queue<CpuWaiter> _waiters = new();
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);
        private TimeSpan _lastCpu;
        private long _lastSampleTimestamp;
        private int _limit = 1;
        private int _active;
        private bool _disposed;

        internal int Active
        {
            get { lock (_gate) return _active; }
        }

        public ResourceGovernor()
        {
            _lastCpu = _process.TotalProcessorTime;
            _lastSampleTimestamp = Stopwatch.GetTimestamp();
        }

        public ValueTask<CpuLease> EnterCpuWorkAsync(CancellationToken token)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                SampleAndAdjustLocked();
                if (_active < _limit)
                {
                    _active++;
                    return ValueTask.FromResult(new CpuLease(this));
                }

                var waiter = new CpuWaiter();
                _waiters.Enqueue(waiter);
                return new ValueTask<CpuLease>(WaitForLeaseAsync(waiter, token));
            }
        }

        private async Task<CpuLease> WaitForLeaseAsync(CpuWaiter waiter, CancellationToken token)
        {
            try
            {
                await waiter.Ready.Task.WaitAsync(token).ConfigureAwait(false);
                return new CpuLease(this);
            }
            catch
            {
                lock (_gate)
                {
                    if (waiter.Granted)
                    {
                        waiter.Granted = false;
                        if (_active <= 0)
                            throw new InvalidOperationException("Contabilidad de CPU inválida durante cancelación.");
                        _active--;
                    }
                    else
                    {
                        waiter.Cancelled = true;
                    }
                    PumpLocked();
                }
                throw;
            }
        }

        private void ReleaseCpuWork()
        {
            lock (_gate)
            {
                if (_active > 0)
                    _active--;
                SampleAndAdjustLocked();
                PumpLocked();
            }
        }

        private void SampleAndAdjustLocked()
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastSampleTimestamp, now);
            if (elapsed < SampleInterval)
                return;

            TimeSpan cpu;
            try
            {
                cpu = _process.TotalProcessorTime;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            var cpuSeconds = Math.Max(0, (cpu - _lastCpu).TotalSeconds);
            var wallSeconds = Math.Max(0.001, elapsed.TotalSeconds);
            var utilization = Math.Clamp(cpuSeconds / (wallSeconds * _processorCount), 0.0, 1.0);
            _lastCpu = cpu;
            _lastSampleTimestamp = now;

            if (utilization >= CpuShrinkThreshold)
                _limit = Math.Max(1, _limit - 1);
            else if (utilization <= CpuGrowThreshold && _waiters.Count > 0)
                _limit = Math.Min(_processorCount, _limit + 1);
        }

        private void PumpLocked()
        {
            while (_active < _limit && _waiters.Count > 0)
            {
                var waiter = _waiters.Dequeue();
                if (waiter.Cancelled)
                    continue;
                waiter.Granted = true;
                _active++;
                waiter.Ready.TrySetResult();
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                while (_waiters.Count > 0)
                {
                    var waiter = _waiters.Dequeue();
                    waiter.Ready.TrySetException(new ObjectDisposedException(nameof(ResourceGovernor)));
                }
            }
            _process.Dispose();
        }

        private sealed class CpuWaiter
        {
            public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Cancelled { get; set; }
            public bool Granted { get; set; }
        }

        internal sealed class CpuLease : IDisposable
        {
            private ResourceGovernor? _owner;

            public CpuLease(ResourceGovernor owner) => _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.ReleaseCpuWork();
            }
        }
    }

    internal sealed class AdaptiveByteBudget
    {
        private readonly object _gate = new();
        private readonly Queue<Waiter> _waiters = new();
        private readonly Func<long, long> _capacityProvider;
        private readonly bool _usesSystemCapacity;
        private long _targetBytes;
        private long _usedBytes;

        internal long UsedBytes
        {
            get { lock (_gate) return _usedBytes; }
        }

        internal long TargetBytes
        {
            get { lock (_gate) return _targetBytes; }
        }

        internal AdaptiveByteBudget(long initialBytes, long maximumBytes)
            : this(initialBytes, _ => maximumBytes, usesSystemCapacity: false)
        {
            if (maximumBytes <= 0 || initialBytes <= 0 || initialBytes > maximumBytes)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        private AdaptiveByteBudget(
            long initialBytes,
            Func<long, long> capacityProvider,
            bool usesSystemCapacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialBytes);
            _capacityProvider = capacityProvider ?? throw new ArgumentNullException(nameof(capacityProvider));
            _usesSystemCapacity = usesSystemCapacity;
            _targetBytes = initialBytes;
        }

        public static AdaptiveByteBudget CreateForSystem()
        {
            var progressQuantum = Math.Max(1, Environment.SystemPageSize);
            var safeNow = MemoryPressureCapacity.GetSafeTotalBytes(0, progressQuantum);
            var initial = Math.Max((long)progressQuantum, Math.Min(InitialBufferBudget, safeNow));
            return new AdaptiveByteBudget(initial, _ => 0, usesSystemCapacity: true);
        }

        internal int GetAdmissibleConcurrency(int bytesPerBlock)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerBlock);
            lock (_gate)
            {
                var capacity = CurrentSafeCapacityLocked(bytesPerBlock);
                var blocks = Math.Max(1L, capacity / bytesPerBlock);
                return (int)Math.Min(int.MaxValue, blocks);
            }
        }

        public ValueTask AcquireAsync(int bytes, CancellationToken token)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            lock (_gate)
            {
                if (_waiters.Count == 0 && TryAcquireLocked(bytes))
                    return ValueTask.CompletedTask;

                var waiter = new Waiter(bytes);
                _waiters.Enqueue(waiter);
                return new ValueTask(WaitAsync(waiter, token));
            }
        }

        public void Release(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            List<Waiter>? ready = null;
            lock (_gate)
            {
                if (_usedBytes < bytes)
                    throw new InvalidOperationException("Se intentó liberar más memoria FAN-OUT de la reservada.");
                _usedBytes -= bytes;
                ready = PumpWaitersLocked();
            }
            Complete(ready);
        }

        private async Task WaitAsync(Waiter waiter, CancellationToken token)
        {
            try
            {
                await waiter.Completion.Task.WaitAsync(token).ConfigureAwait(false);
            }
            catch
            {
                List<Waiter>? ready = null;
                lock (_gate)
                {
                    if (waiter.Granted)
                    {
                        waiter.Granted = false;
                        if (_usedBytes < waiter.Bytes)
                            throw new InvalidOperationException("Contabilidad de memoria inválida durante cancelación.");
                        _usedBytes -= waiter.Bytes;
                    }
                    else
                    {
                        waiter.Cancelled = true;
                    }
                    ready = PumpWaitersLocked();
                }
                Complete(ready);
                throw;
            }
        }

        private bool TryAcquireLocked(int bytes)
        {
            var safeCapacity = CurrentSafeCapacityLocked(bytes);
            var effectiveTarget = Math.Min(_targetBytes, safeCapacity);
            if (_usedBytes + bytes <= effectiveTarget)
            {
                _usedBytes += bytes;
                return true;
            }

            if (TryGrowLocked(bytes, safeCapacity) && _usedBytes + bytes <= _targetBytes)
            {
                _usedBytes += bytes;
                return true;
            }
            return false;
        }

        private bool TryGrowLocked(int bytes, long safeCapacity)
        {
            if (_usedBytes + bytes > safeCapacity || _targetBytes >= safeCapacity)
                return false;

            var doubled = _targetBytes >= long.MaxValue / 2
                ? long.MaxValue
                : _targetBytes * 2;
            var requestedTarget = Math.Max(doubled, _usedBytes + bytes);
            var next = Math.Min(requestedTarget, safeCapacity);
            if (next <= _targetBytes)
                return false;
            _targetBytes = next;
            return true;
        }

        private long CurrentSafeCapacityLocked(int minimumProgressBytes)
        {
            var capacity = _usesSystemCapacity
                ? MemoryPressureCapacity.GetSafeTotalBytes(_usedBytes, minimumProgressBytes)
                : _capacityProvider(_usedBytes);
            return Math.Max(_usedBytes, capacity);
        }

        private List<Waiter>? PumpWaitersLocked()
        {
            List<Waiter>? ready = null;
            while (_waiters.Count > 0)
            {
                var waiter = _waiters.Peek();
                if (waiter.Cancelled)
                {
                    _waiters.Dequeue();
                    continue;
                }
                if (!TryAcquireLocked(waiter.Bytes))
                    break;
                _waiters.Dequeue();
                waiter.Granted = true;
                (ready ??= []).Add(waiter);
            }
            return ready;
        }

        private static void Complete(List<Waiter>? ready)
        {
            if (ready is null)
                return;
            foreach (var waiter in ready)
                waiter.Completion.TrySetResult();
        }

        private sealed class Waiter(int bytes)
        {
            public int Bytes { get; } = bytes;
            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Cancelled { get; set; }
            public bool Granted { get; set; }
        }
    }

    private sealed class DestinationWorker : IDisposable
    {
        public Dictionary<string, VerificationPlan> VerificationPlans { get; } = new(StringComparer.Ordinal);
        private int _active = 1;
        private int _queueDepth;
        private long _pendingPayloadBytes;
        private long _peakPendingPayloadBytes;
        private long _lastProgressTicks = DateTime.UtcNow.Ticks;

        public DestinationWorker(
            string root,
            int slot,
            DestinationProgress progress,
            StorageDeviceInfo device,
            DeviceScheduler deviceScheduler,
            AdaptiveControlByteBudget controlBudget,
            string? replayDirectory)
        {
            Root = root;
            Slot = slot;
            Progress = progress;
            Device = device;
            DeviceScheduler = deviceScheduler;
            ControlBudget = controlBudget ?? throw new ArgumentNullException(nameof(controlBudget));
            ReplayStore = new BranchReplayStore(replayDirectory);
            ReplayGate = new BranchReplayGate();
            Channel = System.Threading.Channels.Channel.CreateUnbounded<FanoutMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        }

        public string Root { get; }
        public int Slot { get; }
        public DestinationProgress Progress { get; }
        public StorageDeviceInfo Device { get; }
        public DeviceScheduler DeviceScheduler { get; }
        internal AdaptiveControlByteBudget ControlBudget { get; }
        internal BranchReplayStore ReplayStore { get; }
        internal BranchReplayGate ReplayGate { get; }
        public Channel<FanoutMessage> Channel { get; }
        public bool IsActive => Volatile.Read(ref _active) != 0;
        public long PendingPayloadBytes => Interlocked.Read(ref _pendingPayloadBytes);
        public long PeakPendingPayloadBytes => Interlocked.Read(ref _peakPendingPayloadBytes);
        public DateTime LastProgressUtc => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);

        public void NoteProgress() => Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);

        public void ReservePendingPayload(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            var pending = Interlocked.Add(ref _pendingPayloadBytes, bytes);
            var peak = Interlocked.Read(ref _peakPendingPayloadBytes);
            while (pending > peak)
            {
                var observed = Interlocked.CompareExchange(ref _peakPendingPayloadBytes, pending, peak);
                if (observed == peak)
                    break;
                peak = observed;
            }
        }

        public void ReleasePendingPayload(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            while (true)
            {
                var current = Interlocked.Read(ref _pendingPayloadBytes);
                if (current < bytes)
                    throw new InvalidOperationException("La rama intentó liberar más payload FAN-OUT del que mantiene pendiente.");
                if (Interlocked.CompareExchange(ref _pendingPayloadBytes, current - bytes, current) == current)
                    return;
            }
        }

        public void IncrementQueueDepth()
        {
            var depth = Interlocked.Increment(ref _queueDepth);
            Progress.SetQueueDepth(depth);
        }

        public void DecrementQueueDepth()
        {
            var depth = Interlocked.Decrement(ref _queueDepth);
            if (depth < 0)
            {
                Interlocked.Exchange(ref _queueDepth, 0);
                throw new InvalidOperationException("La profundidad de cola del destino quedó negativa.");
            }
            Progress.SetQueueDepth(depth);
        }

        public void Fail(string error)
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            Progress.MarkError(error);
            Progress.SetPhase(DestinationPhase.Failed, error);
            Channel.Writer.TryComplete();
        }

        public void Dispose() => ReplayStore.Dispose();
    }

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
