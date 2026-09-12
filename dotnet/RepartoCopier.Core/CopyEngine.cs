using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using Blake3;

namespace RepartoCopier.Core;

public sealed class CopyJob : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);
    private readonly IReadOnlyList<DestinationProgress> _progress;
    private Task _completion = Task.CompletedTask;

    internal CopyJob(IReadOnlyList<DestinationProgress> progress) => _progress = progress;

    public bool IsPaused => !_pauseGate.IsSet;
    public Task Completion => _completion;
    internal CancellationToken Token => _cancel.Token;

    internal void Attach(Task completion) => _completion = completion;

    public IReadOnlyList<DestinationSnapshot> Snapshot() =>
        _progress.Select(item => item.Snapshot()).ToArray();

    public void SetPaused(bool paused)
    {
        if (paused) _pauseGate.Reset();
        else _pauseGate.Set();
    }

    public void RequestCancel()
    {
        _pauseGate.Set();
        _cancel.Cancel();
    }

    internal void WaitIfPaused(CancellationToken token) => _pauseGate.Wait(token);

    public async ValueTask DisposeAsync()
    {
        RequestCancel();
        try { await _completion.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _pauseGate.Dispose();
        _cancel.Dispose();
    }
}

public static class CopyEngine
{
    private const int BlockSize = 16 * 1024 * 1024;
    private const int SourcePrefetchPhysicalCapacity = 4;
    private const int SourcePrefetchThreshold = 16 * 1024 * 1024;
    private const int WriteChunkSize = 4 * 1024 * 1024;
    private const int SmallBufferSize = 64 * 1024;
    private const int MediumBufferSize = 1024 * 1024;
    private const int LargeBufferSize = 4 * 1024 * 1024;
    private const int PreallocationThreshold = 4 * 1024 * 1024;
    private const long MinimumBufferBudget = 256L * 1024 * 1024;
    private const long InitialBufferBudget = 512L * 1024 * 1024;
    private const long MaximumBufferBudget = 4L * 1024 * 1024 * 1024;
    private const long BufferBudgetGrowthStep = 256L * 1024 * 1024;
    private const int ChannelCapacity = 16;
    private const int AdaptiveInitialQueue = 4;
    private const int AdaptiveMinQueue = 2;
    private const int AdaptiveMaxQueue = 8;
    private const int FastSamplesToGrow = 8;
    private const int Retries = 2;
    private static readonly TimeSpan FastBlockWrite = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan SlowBlockWrite = TimeSpan.FromMilliseconds(250);

    public static CopyJob Start(CopyPlan plan, CopyOptions? options = null)
    {
        options ??= new CopyOptions(
            Verify: true,
            SkipSame: plan.SkipSame,
            KeepGoing: plan.KeepGoing);

        var prepared = Preflight(plan);
        var progress = prepared.DestinationRoots
            .Select(root => new DestinationProgress(root, prepared.TotalBytes))
            .ToArray();
        var job = new CopyJob(progress);
        job.Attach(Task.Run(() => RunAsync(prepared, progress, options, job), CancellationToken.None));
        return job;
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
            sourceIsDirectory ? scan : null);
    }

    private static async Task RunAsync(
        PreparedCopy copy,
        DestinationProgress[] progress,
        CopyOptions options,
        CopyJob job)
    {
        var token = job.Token;
        var expectedHashes = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        DestinationWorker[] workers = [];
        using var resources = new ResourceGovernor();
        var pipeline = new PipelineGovernor();
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

            var queueDepth = ChannelCapacity;
            workers = copy.DestinationRoots
                .Select((root, index) => new DestinationWorker(root, index, progress[index], queueDepth))
                .ToArray();

            var writerTasks = workers
                .Select(worker => WriterLoopAsync(worker, options, job, expectedHashes))
                .ToArray();

            Exception? producerError = null;
            try
            {
                await ProducerLoopAsync(copy, workers, progress, skipMasks, expectedHashes, job, pipeline).ConfigureAwait(false);
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

            if (producerError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producerError).Throw();
            if (writerError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writerError).Throw();

            if (options.Verify && !token.IsCancellationRequested)
                await VerifyDestinationsAsync(copy, workers, progress, expectedHashes, job, resources).ConfigureAwait(false);

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
            }
        }
    }

    private static async Task ProducerLoopAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        bool[][] skipMasks,
        ConcurrentDictionary<string, byte[]> expectedHashes,
        CopyJob job,
        PipelineGovernor pipeline)
    {
        var token = job.Token;
        var bufferBudget = AdaptiveByteBudget.CreateForSystem();
        try
        {
            for (var fileIndex = 0; fileIndex < copy.Files.Count; fileIndex++)
            {
                token.ThrowIfCancellationRequested();
                job.WaitIfPaused(token);
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

                await DeliverAsync(active, new BeginMessage(entry), countsData: false, job).ConfigureAwait(false);

                var sourceResult = entry.Size >= SourcePrefetchThreshold
                    ? await ReadAndFanOutPrefetchedAsync(entry, active, bufferBudget, job, pipeline).ConfigureAwait(false)
                    : await ReadAndFanOutSequentialAsync(entry, active, bufferBudget, job, pipeline).ConfigureAwait(false);
                if (sourceResult is null)
                    continue;

                var hash = sourceResult.Hash;
                var key = PathKey(entry.RelativePath);
                if (expectedHashes.TryGetValue(key, out var preflightHash) && !hash.AsSpan().SequenceEqual(preflightHash))
                    throw new IOException($"El origen cambió durante la copia: {entry.RelativePath}");
                expectedHashes[key] = hash;
                await DeliverAsync(active.Where(worker => worker.IsActive).ToArray(), new EndMessage(hash), countsData: false, job).ConfigureAwait(false);
            }

            if (copy.SourceScan is not null)
                PreflightSafety.ValidateSourceTreeSnapshot(copy.SourceRoot, copy.SourceScan);
        }
        finally
        {
            // SharedBlock instances can outlive the producer while destination writers
            // drain their bounded channels. Disposing the semaphore here races with the
            // final SharedBlock.Release() calls and can abort otherwise valid copies.
            // The semaphore is intentionally left for GC once the last shared block and
            // this producer scope release their references.
        }
    }

    private static async Task<SourceReadResult?> ReadAndFanOutSequentialAsync(
        FileEntry entry,
        List<DestinationWorker> active,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline)
    {
        using var hasher = Hasher.New();
        await using var source = OpenSourceStream(entry.SourcePath);
        var readBufferSize = ReadBufferSizeFor(entry.Size);
        long totalRead = 0;

        while (true)
        {
            job.Token.ThrowIfCancellationRequested();
            job.WaitIfPaused(job.Token);
            var budgetStarted = Stopwatch.GetTimestamp();
            await bufferBudget.AcquireAsync(readBufferSize, job.Token).ConfigureAwait(false);
            pipeline.RecordBudgetWait(Stopwatch.GetElapsedTime(budgetStarted));
            var rented = ArrayPool<byte>.Shared.Rent(readBufferSize);
            int read;
            try
            {
                var readStarted = Stopwatch.GetTimestamp();
                read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), job.Token).ConfigureAwait(false);
                pipeline.RecordSourceRead(Stopwatch.GetElapsedTime(readStarted));
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(rented);
                bufferBudget.Release(readBufferSize);
                throw;
            }

            if (read == 0)
            {
                ArrayPool<byte>.Shared.Return(rented);
                bufferBudget.Release(readBufferSize);
                break;
            }

            totalRead += read;
            hasher.Update(rented.AsSpan(0, read));
            var recipients = active.Where(worker => worker.IsActive).ToArray();
            if (recipients.Length == 0)
            {
                ArrayPool<byte>.Shared.Return(rented);
                bufferBudget.Release(readBufferSize);
                return null;
            }

            var block = new SharedBlock(rented, read, readBufferSize, recipients.Length, bufferBudget);
            var deliveryStarted = Stopwatch.GetTimestamp();
            await DeliverAsync(recipients, new DataMessage(block), countsData: true, job).ConfigureAwait(false);
            pipeline.RecordDeliveryWait(Stopwatch.GetElapsedTime(deliveryStarted));
            active.RemoveAll(worker => !worker.IsActive);
            if (active.Count == 0)
                return null;
        }

        ValidateCompletedSourceRead(entry, totalRead);
        return new SourceReadResult(totalRead, hasher.Finalize().AsSpan().ToArray());
    }

    private static async Task<SourceReadResult?> ReadAndFanOutPrefetchedAsync(
        FileEntry entry,
        List<DestinationWorker> active,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline)
    {
        using var prefetchCancel = CancellationTokenSource.CreateLinkedTokenSource(job.Token);
        var sourceQueue = Channel.CreateBounded<SourceReadBlock>(new BoundedChannelOptions(SourcePrefetchPhysicalCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var readTask = PrefetchSourceAsync(
            entry,
            ReadBufferSizeFor(entry.Size),
            sourceQueue.Writer,
            bufferBudget,
            job,
            pipeline,
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
                var recipients = active.Where(worker => worker.IsActive).ToArray();
                if (recipients.Length == 0)
                {
                    sourceBlock.Release();
                    stoppedEarly = true;
                    prefetchCancel.Cancel();
                    break;
                }

                var shared = sourceBlock.TransferToShared(recipients.Length);
                var deliveryStarted = Stopwatch.GetTimestamp();
                await DeliverAsync(recipients, new DataMessage(shared), countsData: true, job).ConfigureAwait(false);
                pipeline.RecordDeliveryWait(Stopwatch.GetElapsedTime(deliveryStarted));
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
        int readBufferSize,
        ChannelWriter<SourceReadBlock> output,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        CancellationToken token)
    {
        Exception? completionError = null;
        try
        {
            using var hasher = Hasher.New();
            await using var source = OpenSourceStream(entry.SourcePath);
            long totalRead = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                job.WaitIfPaused(token);
                await pipeline.AcquirePrefetchSlotAsync(token).ConfigureAwait(false);
                var budgetStarted = Stopwatch.GetTimestamp();
                try
                {
                    await bufferBudget.AcquireAsync(readBufferSize, token).ConfigureAwait(false);
                    pipeline.RecordBudgetWait(Stopwatch.GetElapsedTime(budgetStarted));
                }
                catch
                {
                    pipeline.ReleasePrefetchSlot();
                    throw;
                }
                var rented = ArrayPool<byte>.Shared.Rent(readBufferSize);
                int read;
                try
                {
                    var readStarted = Stopwatch.GetTimestamp();
                    read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), token).ConfigureAwait(false);
                    pipeline.RecordSourceRead(Stopwatch.GetElapsedTime(readStarted));
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(rented);
                    bufferBudget.Release(readBufferSize);
                    pipeline.ReleasePrefetchSlot();
                    throw;
                }

                if (read == 0)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                    bufferBudget.Release(readBufferSize);
                    pipeline.ReleasePrefetchSlot();
                    break;
                }

                totalRead += read;
                hasher.Update(rented.AsSpan(0, read));
                var block = new SourceReadBlock(rented, read, readBufferSize, bufferBudget);
                try
                {
                    await output.WriteAsync(block, token).ConfigureAwait(false);
                }
                catch
                {
                    block.Release();
                    pipeline.ReleasePrefetchSlot();
                    throw;
                }
            }

            ValidateCompletedSourceRead(entry, totalRead);
            return new SourceReadResult(totalRead, hasher.Finalize().AsSpan().ToArray());
        }
        catch (Exception ex)
        {
            completionError = ex;
            throw;
        }
        finally
        {
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
        IReadOnlyCollection<DestinationWorker> recipients,
        FanoutMessage message,
        bool countsData,
        CopyJob job)
    {
        if (recipients.Count == 0)
            return;

        var deliveries = recipients
            .Select(worker => DeliverOneAsync(worker, message, countsData, job))
            .ToArray();
        await Task.WhenAll(deliveries).ConfigureAwait(false);
    }

    private static async Task DeliverOneAsync(
        DestinationWorker worker,
        FanoutMessage message,
        bool countsData,
        CopyJob job)
    {
        if (!worker.IsActive)
        {
            ReleaseIfData(message);
            return;
        }

        job.Token.ThrowIfCancellationRequested();
        job.WaitIfPaused(job.Token);
        var queued = false;
        try
        {
            if (countsData)
            {
                if (!await worker.WaitForAdaptiveWindowAsync(job.Token).ConfigureAwait(false))
                {
                    ReleaseIfData(message);
                    return;
                }
                worker.IncrementQueueDepth();
                queued = true;
            }

            await worker.Channel.Writer.WriteAsync(message, job.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (queued) worker.DecrementQueueDepth();
            ReleaseIfData(message);
            throw;
        }
        catch (ChannelClosedException)
        {
            if (queued) worker.DecrementQueueDepth();
            ReleaseIfData(message);
            if (worker.IsActive)
                worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
        }
        catch
        {
            if (queued) worker.DecrementQueueDepth();
            ReleaseIfData(message);
            throw;
        }
    }

    private static async Task WriterLoopAsync(
        DestinationWorker worker,
        CopyOptions options,
        CopyJob job,
        ConcurrentDictionary<string, byte[]> expectedHashes)
    {
        CurrentFile? current = null;
        using var recovery = new RecoveryCheckpointWriter(worker.Root);
        try
        {
            worker.Progress.SetPhase(DestinationPhase.Copying);
            await foreach (var message in worker.Channel.Reader.ReadAllAsync())
            {
                if (message is DataMessage droppedData && !worker.IsActive)
                {
                    worker.DecrementQueueDepth();
                    droppedData.Block.Release();
                    continue;
                }
                if (!worker.IsActive) continue;

                job.Token.ThrowIfCancellationRequested();
                job.WaitIfPaused(job.Token);
                switch (message)
                {
                    case BeginMessage begin:
                        current = BeginFile(worker, begin.Entry);
                        break;
                    case DataMessage chunkData when current is not null:
                        try
                        {
                            if (!current.Failed)
                            {
                                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                                await WriteWithRetryAsync(worker, current, chunkData.Block.Memory, job).ConfigureAwait(false);
                                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                                if (chunkData.Block.Length >= WriteChunkSize)
                                    worker.RecordBlockWrite(elapsed);
                                current.Copied += chunkData.Block.Length;
                                worker.Progress.AddWritten(chunkData.Block.Length);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            current.Failed = true;
                            current.Stream?.Dispose();
                            current.Stream = null;
                            TryDelete(current.PartPath);
                            if (current.Copied > 0) worker.Progress.RollbackWritten((ulong)current.Copied);
                            if (options.KeepGoing) worker.Progress.MarkError(ex.Message);
                            else worker.Fail(ex.Message);
                        }
                        finally
                        {
                            worker.DecrementQueueDepth();
                            chunkData.Block.Release();
                        }
                        break;
                    case DataMessage orphanData:
                        worker.DecrementQueueDepth();
                        orphanData.Block.Release();
                        break;
                    case EndMessage end when current is not null:
                        FinishFile(worker, current, end.Hash, options, recovery);
                        if (!current.Failed)
                            expectedHashes[PathKey(current.Entry.RelativePath)] = end.Hash;
                        current = null;
                        break;
                }
                worker.NoteProgress();
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
                current.Stream?.Dispose();
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
        var part = StateLayout.PartPath(worker.Root, destination);
        TryDelete(part);
        var stream = new FileStream(part, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1,
            PreallocationSize = entry.Size >= PreallocationThreshold ? entry.Size : 0,
        });
        return new CurrentFile(entry, destination, part, stream);
    }

    private static async Task WriteWithRetryAsync(
        DestinationWorker worker,
        CurrentFile current,
        ReadOnlyMemory<byte> data,
        CopyJob job)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= Retries; attempt++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.WaitIfPaused(job.Token);
            try
            {
                current.Stream ??= ReopenPart(current.PartPath, current.Copied);
                var remaining = data;
                while (!remaining.IsEmpty)
                {
                    var length = Math.Min(WriteChunkSize, remaining.Length);
                    await current.Stream.WriteAsync(remaining[..length], job.Token).ConfigureAwait(false);
                    remaining = remaining[length..];
                    worker.NoteProgress();
                }
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                current.Stream?.Dispose();
                current.Stream = null;
                using (var reset = new FileStream(current.PartPath, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    reset.SetLength(current.Copied);
                    reset.Flush(flushToDisk: true);
                }
                if (attempt < Retries)
                {
                    worker.Progress.AddRetry();
                    await Task.Delay(75 * (attempt + 1), job.Token).ConfigureAwait(false);
                }
            }
        }
        throw new IOException($"No se pudo escribir {current.Entry.RelativePath} después de reintentos.", last);
    }

    private static void FinishFile(
        DestinationWorker worker,
        CurrentFile current,
        byte[] expectedHash,
        CopyOptions options,
        RecoveryCheckpointWriter recovery)
    {
        if (current.Failed) return;
        if (current.Copied != current.Entry.Size)
        {
            current.Failed = true;
            current.Stream?.Dispose();
            current.Stream = null;
            TryDelete(current.PartPath);
            worker.Progress.RollbackWritten((ulong)current.Copied);
            var error = $"Tamaño inesperado en {current.Entry.RelativePath}";
            if (options.KeepGoing) worker.Progress.MarkError(error);
            else worker.Fail(error);
            return;
        }

        if (current.Stream is not null)
        {
            // BufferSize=1 disables FileStream buffering; one durable flush is enough.
            current.Stream.Flush(flushToDisk: true);
            current.Stream.Dispose();
            current.Stream = null;
        }

        var actualSize = new FileInfo(current.PartPath).Length;
        if (actualSize != current.Entry.Size)
            throw new IOException($"Tamaño físico incorrecto en {current.PartPath}: esperado {current.Entry.Size}, obtenido {actualSize}.");

        ValidateRuntimeDestinationPath(worker.Root, current.Entry.RelativePath);
        CommitPart(worker.Root, current.PartPath, current.DestinationPath);
        File.SetLastWriteTimeUtc(current.DestinationPath, current.Entry.LastWriteTimeUtc);
        recovery.Append(
            new RecoveryFile(
                current.Entry.SourcePath,
                current.Entry.RelativePath,
                current.Entry.Size,
                current.Entry.ModifiedUnixNanoseconds),
            expectedHash);
        worker.Progress.MarkDone();
    }

    private static async Task VerifyDestinationsAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        ConcurrentDictionary<string, byte[]> expectedHashes,
        CopyJob job,
        ResourceGovernor resources)
    {
        var activeSlots = Enumerable.Range(0, workers.Length)
            .Where(slot => workers[slot].IsActive)
            .ToArray();
        var tasks = activeSlots.Select(async slot =>
        {
            progress[slot].SetPhase(DestinationPhase.Verifying);
            foreach (var entry in copy.Files)
            {
                job.Token.ThrowIfCancellationRequested();
                job.WaitIfPaused(job.Token);
                if (!expectedHashes.TryGetValue(PathKey(entry.RelativePath), out var expected))
                    continue;
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
                var actual = await HashFileAsync(destination, job.Token, resources).ConfigureAwait(false);
                if (!actual.AsSpan().SequenceEqual(expected))
                {
                    workers[slot].Fail($"BLAKE3 no coincide: {destination}");
                    break;
                }
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
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
            job.WaitIfPaused(token);
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
        ResourceGovernor? resources = null)
    {
        using var hasher = Hasher.New();
        var buffer = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024);
        try
        {
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 1,
            });
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read == 0) break;
                if (resources is null)
                {
                    hasher.Update(buffer.AsSpan(0, read));
                }
                else
                {
                    using var lease = await resources.EnterCpuWorkAsync(token).ConfigureAwait(false);
                    hasher.Update(buffer.AsSpan(0, read));
                }
            }
            return hasher.Finalize().AsSpan().ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void CommitPart(string destinationRoot, string part, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Move(part, destination);
            return;
        }

        WindowsPath.EnsureRegularFile(destination, "El archivo de destino");
        var backup = StateLayout.BackupPath(destinationRoot, destination);
        TryDelete(backup);
        File.Move(destination, backup);
        try
        {
            File.Move(part, destination);
            TryDelete(backup);
        }
        catch (Exception commitError)
        {
            try
            {
                File.Move(backup, destination);
            }
            catch (Exception restoreError)
            {
                throw new IOException(
                    $"CRÍTICO: falló el reemplazo de {destination} ({commitError.Message}) y también restaurar {backup} ({restoreError.Message}). El backup permanece en {backup}.",
                    new AggregateException(commitError, restoreError));
            }
            throw new IOException($"No se pudo reemplazar {destination}; el original fue restaurado.", commitError);
        }
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

    private static int ReadBufferSizeFor(long fileSize) =>
        fileSize <= SmallBufferSize ? SmallBufferSize :
        fileSize <= MediumBufferSize ? MediumBufferSize :
        fileSize <= LargeBufferSize ? LargeBufferSize :
        BlockSize;

    private static FileStream ReopenPart(string path, long offset)
    {
        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1,
        });
        stream.Position = offset;
        return stream;
    }

    private static void DrainAndRelease(DestinationWorker worker)
    {
        while (worker.Channel.Reader.TryRead(out var message))
        {
            if (message is DataMessage data)
            {
                worker.DecrementQueueDepth();
                data.Block.Release();
            }
        }
    }

    private static void ReleaseIfData(FanoutMessage message)
    {
        if (message is DataMessage data) data.Block.Release();
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
        SourceTreeScan? SourceScan);

    private sealed record FileEntry(
        string SourcePath,
        string RelativePath,
        long Size,
        DateTime LastWriteTimeUtc,
        long ModifiedUnixNanoseconds);

    private sealed record SourceReadResult(long BytesRead, byte[] Hash);

    private sealed class SourceReadBlock
    {
        private byte[]? _buffer;
        private readonly int _reservedBytes;
        private readonly AdaptiveByteBudget _budget;

        public SourceReadBlock(byte[] buffer, int length, int reservedBytes, AdaptiveByteBudget budget)
        {
            _buffer = buffer;
            Length = length;
            _reservedBytes = reservedBytes;
            _budget = budget;
        }

        public int Length { get; }

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
            ArrayPool<byte>.Shared.Return(buffer);
            _budget.Release(_reservedBytes);
        }
    }

    private abstract record FanoutMessage;
    private sealed record BeginMessage(FileEntry Entry) : FanoutMessage;
    private sealed record DataMessage(SharedBlock Block) : FanoutMessage;
    private sealed record EndMessage(byte[] Hash) : FanoutMessage;

    internal sealed class SharedBlock
    {
        private byte[]? _buffer;
        private int _references;
        private readonly int _reservedBytes;
        private readonly AdaptiveByteBudget _budget;

        public SharedBlock(byte[] buffer, int length, int reservedBytes, int references, AdaptiveByteBudget budget)
        {
            _buffer = buffer;
            Length = length;
            _reservedBytes = reservedBytes;
            _references = references;
            _budget = budget;
        }

        public int Length { get; }
        public ReadOnlyMemory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(SharedBlock))).AsMemory(0, Length);

        public void Release()
        {
            var remaining = Interlocked.Decrement(ref _references);
            if (remaining > 0)
                return;
            if (remaining < 0)
                throw new InvalidOperationException("SharedBlock liberado más veces que referencias asignadas.");

            var buffer = Interlocked.Exchange(ref _buffer, null)
                ?? throw new InvalidOperationException("SharedBlock perdió su buffer antes de la última liberación.");
            ArrayPool<byte>.Shared.Return(buffer);
            _budget.Release(_reservedBytes);
        }
    }

    internal sealed class PipelineGovernor
    {
        private const int MinPrefetch = 1;
        private const int InitialPrefetch = 2;
        private const int MaxPrefetch = SourcePrefetchPhysicalCapacity;
        private const int SamplesPerDecision = 8;

        private readonly object _gate = new();
        private readonly Queue<PrefetchWaiter> _slotWaiters = new();
        private int _prefetchLimit = InitialPrefetch;
        private int _inFlight;
        private int _samples;
        private double _consumerWaitMs;
        private double _deliveryWaitMs;
        private double _budgetWaitMs;
        private double _readMs;

        internal int InFlight
        {
            get { lock (_gate) return _inFlight; }
        }

        public ValueTask AcquirePrefetchSlotAsync(CancellationToken token)
        {
            lock (_gate)
            {
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
                    case SampleKind.Consumer: _consumerWaitMs += milliseconds; break;
                    case SampleKind.Delivery: _deliveryWaitMs += milliseconds; break;
                    case SampleKind.Budget: _budgetWaitMs += milliseconds; break;
                    case SampleKind.Read: _readMs += milliseconds; break;
                }
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

            if (pressure > starvation * 1.5 && pressure > sourceCost)
                _prefetchLimit = Math.Max(MinPrefetch, _prefetchLimit - 1);
            else if (starvation > pressure * 1.5 && starvation > sourceCost * 0.25)
                _prefetchLimit = Math.Min(MaxPrefetch, _prefetchLimit + 1);

            _samples = 0;
            _consumerWaitMs = 0;
            _deliveryWaitMs = 0;
            _budgetWaitMs = 0;
            _readMs = 0;
            PumpSlotsLocked();
        }

        private void PumpSlotsLocked()
        {
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
        private readonly long _maximumBytes;
        private long _targetBytes;
        private long _usedBytes;

        internal long UsedBytes
        {
            get { lock (_gate) return _usedBytes; }
        }

        internal AdaptiveByteBudget(long initialBytes, long maximumBytes)
        {
            _targetBytes = initialBytes;
            _maximumBytes = maximumBytes;
        }

        public static AdaptiveByteBudget CreateForSystem()
        {
            var memory = GetMemoryStatus();
            var total = checked((long)Math.Min(memory.ullTotalPhys, (ulong)long.MaxValue));
            var available = checked((long)Math.Min(memory.ullAvailPhys, (ulong)long.MaxValue));
            var maximum = Math.Clamp(total / 8, MinimumBufferBudget, MaximumBufferBudget);
            var reserve = Math.Max(2L * 1024 * 1024 * 1024, total / 4);
            var safeNow = Math.Max(MinimumBufferBudget, available - reserve);
            maximum = Math.Max(MinimumBufferBudget, Math.Min(maximum, safeNow));
            var initial = Math.Min(InitialBufferBudget, maximum);
            return new AdaptiveByteBudget(initial, maximum);
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
            if (_usedBytes + bytes <= _targetBytes)
            {
                _usedBytes += bytes;
                return true;
            }

            if (TryGrowLocked(bytes) && _usedBytes + bytes <= _targetBytes)
            {
                _usedBytes += bytes;
                return true;
            }
            return false;
        }

        private bool TryGrowLocked(int bytes)
        {
            if (_targetBytes >= _maximumBytes)
                return false;

            var memory = GetMemoryStatus();
            var available = checked((long)Math.Min(memory.ullAvailPhys, (ulong)long.MaxValue));
            var total = checked((long)Math.Min(memory.ullTotalPhys, (ulong)long.MaxValue));
            var reserve = Math.Max(2L * 1024 * 1024 * 1024, total / 4);
            var headroom = available - reserve;
            if (headroom < BufferBudgetGrowthStep)
                return false;

            var requestedTarget = Math.Max(_targetBytes + BufferBudgetGrowthStep, _usedBytes + bytes);
            var safeTarget = _usedBytes + headroom;
            var next = Math.Min(_maximumBytes, Math.Min(requestedTarget, safeTarget));
            if (next <= _targetBytes)
                return false;
            _targetBytes = next;
            return true;
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

        private static MemoryStatusEx GetMemoryStatus()
        {
            var status = new MemoryStatusEx
            {
                dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>(),
            };
            if (!GlobalMemoryStatusEx(ref status))
                throw new IOException($"No se pudo consultar la memoria física de Windows: {Marshal.GetLastWin32Error()}.");
            return status;
        }

        private sealed class Waiter(int bytes)
        {
            public int Bytes { get; } = bytes;
            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Cancelled { get; set; }
            public bool Granted { get; set; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
    }

    private sealed class DestinationWorker
    {
        private readonly SemaphoreSlim _queueDrained = new(0, 1);
        private int _active = 1;
        private int _queueDepth;
        private int _adaptiveQueueLimit = AdaptiveInitialQueue;
        private int _fastSamples;
        private long _lastProgressTicks = DateTime.UtcNow.Ticks;

        public DestinationWorker(string root, int slot, DestinationProgress progress, int capacity)
        {
            Root = root;
            Slot = slot;
            Progress = progress;
            Channel = System.Threading.Channels.Channel.CreateBounded<FanoutMessage>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        }

        public string Root { get; }
        public int Slot { get; }
        public DestinationProgress Progress { get; }
        public Channel<FanoutMessage> Channel { get; }
        public bool IsActive => Volatile.Read(ref _active) != 0;
        public DateTime LastProgressUtc => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);
        private int QueueDepth => Math.Max(0, Volatile.Read(ref _queueDepth));
        private int AdaptiveQueueLimit => Volatile.Read(ref _adaptiveQueueLimit);

        public void NoteProgress() => Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);

        public async ValueTask<bool> WaitForAdaptiveWindowAsync(CancellationToken token)
        {
            while (IsActive && QueueDepth >= AdaptiveQueueLimit)
                await _queueDrained.WaitAsync(token).ConfigureAwait(false);
            return IsActive;
        }

        public void RecordBlockWrite(TimeSpan elapsed)
        {
            if (elapsed >= SlowBlockWrite)
            {
                Interlocked.Exchange(ref _fastSamples, 0);
                AdjustQueueLimit(-1);
                return;
            }

            if (elapsed <= FastBlockWrite)
            {
                if (Interlocked.Increment(ref _fastSamples) >= FastSamplesToGrow)
                {
                    Interlocked.Exchange(ref _fastSamples, 0);
                    AdjustQueueLimit(1);
                }
                return;
            }

            Interlocked.Exchange(ref _fastSamples, 0);
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
            PulseQueueDrained();
        }

        public void Fail(string error)
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            Progress.MarkError(error);
            Progress.SetPhase(DestinationPhase.Failed, error);
            Channel.Writer.TryComplete();
            PulseQueueDrained();
        }

        private void AdjustQueueLimit(int delta)
        {
            while (true)
            {
                var current = Volatile.Read(ref _adaptiveQueueLimit);
                var next = Math.Clamp(current + delta, AdaptiveMinQueue, AdaptiveMaxQueue);
                if (next == current)
                    return;
                if (Interlocked.CompareExchange(ref _adaptiveQueueLimit, next, current) == current)
                {
                    if (next > current)
                        PulseQueueDrained();
                    return;
                }
            }
        }

        private void PulseQueueDrained()
        {
            if (_queueDrained.CurrentCount != 0)
                return;
            try { _queueDrained.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    private sealed class CurrentFile(
        FileEntry entry,
        string destinationPath,
        string partPath,
        FileStream stream)
    {
        public FileEntry Entry { get; } = entry;
        public string DestinationPath { get; } = destinationPath;
        public string PartPath { get; } = partPath;
        public FileStream? Stream { get; set; } = stream;
        public long Copied { get; set; }
        public bool Failed { get; set; }
    }
}

