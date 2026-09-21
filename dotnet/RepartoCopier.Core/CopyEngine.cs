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

    private const int SharedFanoutBlockBytes = 8 * 1024 * 1024;
    private const long SharedFanoutPoolBytes = 256L * 1024 * 1024;
    private const int VerificationWorkspaceBytes = 8 * 1024 * 1024;


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
        SharedFanoutBufferPool? bufferPool = null;
        var controlBudget = AdaptiveControlByteBudget.CreateForSystem();
        var spillBudget = new FanoutSpillBudget();
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

            workers = copy.DestinationRoots
                .Select((root, index) => new DestinationWorker(
                    root,
                    index,
                    progress[index],
                    copy.DestinationDevices[index],
                    deviceSchedulers.For(copy.DestinationDevices[index]),
                    controlBudget,
                    spillBudget,
                    copy.DestinationRoots.Length))
                .ToArray();

            var copyPhaseStarted = Stopwatch.GetTimestamp();
            var activeBufferPool = bufferPool = new SharedFanoutBufferPool(checked((int)SharedFanoutPoolBytes));
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
                    activeBufferPool,
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

            ValidateFanoutDrain(workers, spillBudget, activeBufferPool);

            if (producerError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producerError).Throw();
            if (writerError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writerError).Throw();

            // COPY owns the large 256 MiB page pool. Verification deliberately does not.
            // Once every writer drained, release that pinned/locked region before VERIFY.
            activeBufferPool.Dispose();
            bufferPool = null;

            if (options.Verify && !token.IsCancellationRequested)
            {
                var verifyPhaseStarted = Stopwatch.GetTimestamp();
                try
                {
                    await VerifyDestinationsAsync(
                        copy, workers, progress, job, deviceSchedulers.SharedSourceScheduler).ConfigureAwait(false);
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
                DrainAndRelease(worker.Channel.Reader, worker);
            }
            bufferPool?.Dispose();
            copy.ReleaseStateLeases();
        }
    }

    private static void ValidateFanoutDrain(
        IReadOnlyList<DestinationWorker> workers,
        FanoutSpillBudget spillBudget,
        SharedFanoutBufferPool bufferPool)
    {
        var failures = new List<string>();
        foreach (var worker in workers)
        {
            if (worker.PendingPayloadBytes != 0)
                failures.Add($"{worker.Root}: pending={worker.PendingPayloadBytes}");
            if (worker.SpillBytes != 0)
                failures.Add($"{worker.Root}: spill={worker.SpillBytes}");
            if (worker.DeviceScheduler.QueuedBytes != 0)
                failures.Add($"{worker.Root}: queued={worker.DeviceScheduler.QueuedBytes}");
        }
        if (spillBudget.UsedBytes != 0)
            failures.Add($"spill-global={spillBudget.UsedBytes}");
        if (bufferPool.UsedBytes != 0)
            failures.Add($"shared-pool={bufferPool.UsedBytes}");

        if (failures.Count != 0)
            throw new InvalidOperationException(
                "FAN-OUT terminó con recursos retenidos: " + string.Join(", ", failures));
    }

    private static async Task ProducerLoopAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        bool[][] skipMasks,
        Dictionary<string, byte[]> expectedHashes,
        CopyJob job,
        SharedFanoutBufferPool bufferPool,
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
                var readBufferSize = SelectSharedFanoutBlockSize(entry.Size, transferAlignment);
                job.Telemetry.RecordTransferSize(readBufferSize);

                var sourceResult = await ReadAndFanOutSequentialAsync(
                    entry,
                    copy.SourceDevice,
                    active,
                    readBufferSize,
                    transferAlignment,
                    bufferPool,
                    job,
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
        SharedFanoutBufferPool bufferPool,
        CopyJob job,
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
                active.RemoveAll(worker => !worker.IsActive);
                if (active.Count == 0) return null;

                var normal = new List<DestinationWorker>(active.Count);
                var spilling = new List<DestinationWorker>();
                foreach (var worker in active)
                {
                    if (worker.SpillController.ShouldSpill(
                        worker.DeviceScheduler.QueuedBytes,
                        worker.DeviceScheduler.BacklogTargetBytes,
                        worker.SpillBytes))
                        spilling.Add(worker);
                    else
                        normal.Add(worker);
                }

                // A producer reference is used only when every active destination is
                // isolated. It gives the source a temporary aligned page to read/copy
                // from without making any slow destination retain the shared pool.
                var reservedReferences = Math.Max(1, normal.Count);
                var poolStarted = Stopwatch.GetTimestamp();
                SharedFanoutBufferPool.Lease? lease = await bufferPool.RentAsync(
                    readBufferSize, transferAlignment, reservedReferences, job.Token).ConfigureAwait(false);
                job.Telemetry.RecordBufferWait(Stopwatch.GetElapsedTime(poolStarted));
                job.Telemetry.ObserveBuffer(bufferPool.UsedBytes, bufferPool.CapacityBytes);

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
                                read = await direct.ReadAsync(lease.Buffer, readBufferSize, totalRead, job.Token).ConfigureAwait(false);
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
                            read = await buffered!.ReadAsync(lease.Memory[..remaining], job.Token).ConfigureAwait(false);
                    }
                    finally { sourceIo?.Dispose(); }

                    job.Telemetry.RecordSourceRead(read, Stopwatch.GetElapsedTime(readStarted));
                    if (read == 0) throw new IOException($"Lectura incompleta del origen: {entry.RelativePath}");
                    totalRead += read;
                    var hashStarted = Stopwatch.GetTimestamp();
                    hasher.UpdateWithJoin(lease.Memory.Span[..read]);
                    job.Telemetry.RecordSourceHash(read, Stopwatch.GetElapsedTime(hashStarted));

                    // Private copies are made before shared delivery. They never retain a
                    // shared-pool reference, so a lagging branch cannot stall fast peers.
                    foreach (var worker in spilling.ToArray())
                    {
                        if (!worker.IsActive) continue;
                        if (!worker.TryReserveSpill(read))
                        {
                            worker.Fail($"Destino {worker.Root}: abortado — no pudo sostener el ritmo mínimo de su propia clase de hardware");
                            continue;
                        }

                        FanoutSpillBlock privateBlock;
                        try
                        {
                            privateBlock = FanoutSpillBlock.CopyFrom(lease.Memory[..read], transferAlignment);
                        }
                        catch
                        {
                            worker.ReleaseSpill(read);
                            throw;
                        }

                        // Ownership (including the spill reservation) transfers to
                        // delivery before any enqueue operation can fail.
                        await DeliverSingleDataAsync(
                            worker,
                            new DataMessage(new SpillBlock(privateBlock)),
                            job).ConfigureAwait(false);
                    }

                    normal.RemoveAll(worker => !worker.IsActive);
                    if (normal.Count > 0)
                    {
                        var released = reservedReferences - normal.Count;
                        for (var n = 0; n < released; n++) lease.ReleaseReference();
                        var block = new SharedBlock(lease, read);
                        lease = null;
                        await DeliverDataAsync(normal, new DataMessage(block), job).ConfigureAwait(false);
                    }
                    else
                    {
                        // Release either the temporary producer reference or references
                        // belonging to normal branches that failed before delivery.
                        for (var n = 0; n < reservedReferences; n++) lease.ReleaseReference();
                        lease = null;
                    }

                    active.RemoveAll(worker => !worker.IsActive);
                    if (active.Count == 0) return null;
                }
                finally { lease?.Dispose(); }
            }

            ValidateCompletedSourceRead(entry, totalRead);
            return new SourceReadResult(totalRead, hasher.Finalize().AsSpan().ToArray());
        }
        finally
        {
            direct?.Dispose();
            if (buffered is not null) await buffered.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task DeliverSingleDataAsync(DestinationWorker worker, DataMessage message, CopyJob job)
    {
        var payloadOwned = false;
        var queueOwned = false;
        var blockOwned = true;
        try
        {
            if (!worker.IsActive) return;
            worker.DeviceScheduler.ReserveBacklog(message.Block.Length);
            worker.ReservePendingPayload(message.Block.Length);
            payloadOwned = true;
            worker.IncrementQueueDepth();
            queueOwned = true;
            if (worker.Channel.Writer.TryWrite(message))
            {
                payloadOwned = false;
                queueOwned = false;
                blockOwned = false;
                return;
            }
            if (worker.IsActive) worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
        }
        finally
        {
            if (queueOwned) worker.DecrementQueueDepth();
            if (payloadOwned) ReleaseBranchPayload(worker, message.Block.Length);
            if (blockOwned)
            {
                var spill = message.Block.IsSpill;
                var length = message.Block.Length;
                message.Block.Release();
                if (spill) worker.ReleaseSpill(length);
            }
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

                var payloadOwned = false;
                var queueOwned = false;
                var blockOwned = true;
                try
                {
                    worker.DeviceScheduler.ReserveBacklog(message.Block.Length);
                    worker.ReservePendingPayload(message.Block.Length);
                    payloadOwned = true;
                    worker.IncrementQueueDepth();
                    queueOwned = true;

                    if (worker.Channel.Writer.TryWrite(message))
                    {
                        payloadOwned = false;
                        queueOwned = false;
                        blockOwned = false;
                        continue;
                    }

                    worker.DecrementQueueDepth();
                    queueOwned = false;
                    ReleaseBranchPayload(worker, message.Block.Length);
                    payloadOwned = false;
                    message.Block.Release();
                    blockOwned = false;
                    if (worker.IsActive)
                        worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
                }
                catch
                {
                    if (queueOwned)
                        worker.DecrementQueueDepth();
                    if (payloadOwned)
                        ReleaseBranchPayload(worker, message.Block.Length);
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
                var dataOwnedByWriter = data is not null;
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

                            var admissionError = await EnsureWriteWindowAsync(worker, current, job).ConfigureAwait(false);
                            if (admissionError is not null)
                            {
                                FailCurrentFile(worker, current, options, admissionError.Message);
                                break;
                            }

                            var offset = current.ReserveWriteOffset(chunkData.Block.Length);
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
                    if (dataOwnedByWriter && data is not null)
                        ReleaseBranchBlock(worker, data.Block);
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
            DrainAndRelease(worker.Channel.Reader, worker);
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

    private static async Task<PendingWriteResult> WriteBlockAtOffsetAsync(
        DestinationWorker worker,
        CurrentFile current,
        FanoutBlock block,
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
                var directRetryCount = 0;
                var lastTransientCode = 0;
                int operations;
                while (true)
                {
                    try
                    {
                        operations = await direct.WriteAsync(
                            data,
                            offset,
                            current.Entry.Size,
                            payloadIsAligned: true,
                            worker.DeviceScheduler,
                            job.Token).ConfigureAwait(false);
                        if (directRetryCount > 0)
                            job.Telemetry.RecordIoRecovery(
                                "copy-write", current.Entry.RelativePath, "direct", lastTransientCode,
                                worker.DeviceScheduler.CurrentQueueDepth, directRetryCount, offset, recovered: true);
                        break;
                    }
                    catch (Exception ex) when (TransientIoErrorClassifier.IsTransient(ex))
                    {
                        var failedQueueDepth = worker.DeviceScheduler.CurrentQueueDepth;
                        lastTransientCode = TransientIoErrorClassifier.GetNativeCodeOrZero(ex);
                        directRetryCount++;
                        worker.Progress.AddRetry();
                        job.Telemetry.RecordIoRecovery(
                            "copy-write", current.Entry.RelativePath, "direct", lastTransientCode,
                            failedQueueDepth, directRetryCount, offset, recovered: false);
                        await DelayTransientRetryAsync(directRetryCount, job.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (DirectIoDestinationWriter.IsFallbackable(ex))
                    {
                        current.RequestDirectFallback();
                        return PendingWriteResult.NeedsBufferedRetry(block, offset, ex);
                    }
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

        var bufferedRetryCount = 0;
        var lastBufferedTransientCode = 0;
        while (true)
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
                if (bufferedRetryCount > 0)
                    job.Telemetry.RecordIoRecovery(
                        "copy-write", current.Entry.RelativePath, "buffered", lastBufferedTransientCode,
                        worker.DeviceScheduler.CurrentQueueDepth, bufferedRetryCount, offset, recovered: true);
                return PendingWriteResult.Success();
            }
            catch (Exception ex)
            {
                last = ex;
                if (!TransientIoErrorClassifier.IsTransient(ex))
                    break;

                var failedQueueDepth = worker.DeviceScheduler.CurrentQueueDepth;
                lastBufferedTransientCode = TransientIoErrorClassifier.GetNativeCodeOrZero(ex);
                bufferedRetryCount++;
                worker.Progress.AddRetry();
                job.Telemetry.RecordIoRecovery(
                    "copy-write", current.Entry.RelativePath, "buffered", lastBufferedTransientCode,
                    failedQueueDepth, bufferedRetryCount, offset, recovered: false);
                await DelayTransientRetryAsync(bufferedRetryCount, job.Token).ConfigureAwait(false);
            }
        }

        ReleaseBranchBlock(worker, block);
        return PendingWriteResult.Failed(
            new IOException($"No se pudo escribir {current.Entry.RelativePath} en offset {offset} después de reintentos.", last));
    }

    private static async Task<Exception?> EnsureWriteWindowAsync(
        DestinationWorker worker,
        CurrentFile current,
        CopyJob job)
    {
        PruneCompletedSuccesses(current);
        var admissionWindow = Math.Max(1, worker.DeviceScheduler.ExplorationQueueDepth);
        if (current.PendingWrites.Count < admissionWindow)
            return null;
        return await DrainPendingWritesAsync(worker, current, job).ConfigureAwait(false);
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

    private static void ReleaseRetryBlock(DestinationWorker worker, FanoutBlock? block)
    {
        if (block is not null)
            ReleaseBranchBlock(worker, block);
    }

    private static void ReleaseBranchBlock(DestinationWorker worker, FanoutBlock block)
    {
        var length = block.Length;
        var spill = block.IsSpill;
        ReleaseBranchPayload(worker, length);
        block.Release();
        if (spill) worker.ReleaseSpill(length);
    }

    private static void ReleaseBranchPayload(DestinationWorker worker, int bytes)
    {
        worker.ReleasePendingPayload(bytes);
        worker.DeviceScheduler.ReleaseBacklog(bytes);
    }

    private static void ReleaseQueuedPayload(DestinationWorker worker, FanoutMessage message)
    {
        if (message is DataMessage data)
            ReleaseBranchBlock(worker, data.Block);
        else
            ReleaseQueuedControl(message);
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
        worker.CompletedFiles.Add(PathKey(current.Entry.RelativePath));
        worker.Progress.MarkDone();
    }

    private static async Task VerifyDestinationsAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        CopyJob job,
        DeviceScheduler? sharedSourceScheduler)
    {
        for (var slot = 0; slot < workers.Length; slot++)
        {
            if (!workers[slot].IsActive)
                continue;
            var entries = copy.Files
                .Where(entry => workers[slot].CompletedFiles.Contains(PathKey(entry.RelativePath)))
                .ToArray();
            var bytes = entries.Aggregate<FileEntry, ulong>(0, (sum, entry) => checked(sum + (ulong)entry.Size));
            progress[slot].SetVerifyWork(bytes, (ulong)entries.Length);
            if (entries.Length > 0)
                progress[slot].SetPhase(DestinationPhase.Verifying);
        }

        foreach (var entry in copy.Files)
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

            var slots = Enumerable.Range(0, workers.Length)
                .Where(slot => workers[slot].IsActive && workers[slot].CompletedFiles.Contains(PathKey(entry.RelativePath)))
                .ToArray();
            if (slots.Length == 0)
                continue;

            ValidateSourceSnapshot(entry);
            var targets = new List<CoordinatedVerifyTarget>(slots.Length + 1);
            try
            {
                targets.Add(new CoordinatedVerifyTarget(
                    slot: -1,
                    path: entry.SourcePath,
                    device: copy.SourceDevice,
                    scheduler: sharedSourceScheduler,
                    progress: null));

                foreach (var slot in slots)
                {
                    progress[slot].SetLastFile(entry.RelativePath);
                    var destination = Path.Combine(workers[slot].Root, entry.RelativePath);
                    if (!File.Exists(destination))
                    {
                        workers[slot].Fail($"Falta el archivo durante verificación: {destination}");
                        continue;
                    }
                    ValidateRuntimeDestinationPath(workers[slot].Root, entry.RelativePath);
                    WindowsPath.EnsureRegularFile(destination, "El archivo durante verificación");
                    if (new FileInfo(destination).Length != entry.Size)
                    {
                        workers[slot].Fail($"Tamaño no coincide durante verificación: {destination}");
                        continue;
                    }
                    targets.Add(new CoordinatedVerifyTarget(
                        slot,
                        destination,
                        copy.DestinationDevices[slot],
                        workers[slot].DeviceScheduler,
                        progress[slot]));
                }

                if (targets.Count <= 1)
                    continue;

                using var workspace = new VerificationWorkspace(targets);
                var perStreamBytes = workspace.PerStreamBytes;
                for (var index = 0; index < targets.Count; index++)
                    targets[index].Open(workspace.RentSlice(index));

                long offset = 0;
                while (offset < entry.Size)
                {
                    job.Token.ThrowIfCancellationRequested();
                    await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

                    var activeTargets = targets
                        .Where(target => target.IsSource || workers[target.Slot].IsActive)
                        .ToArray();
                    if (activeTargets.Length <= 1)
                        break;

                    var expectedBytes = (int)Math.Min(perStreamBytes, entry.Size - offset);
                    var reads = activeTargets
                        .Select(target => ReadVerifyTargetAsync(target, expectedBytes, offset, job))
                        .ToArray();
                    var results = await Task.WhenAll(reads).ConfigureAwait(false);

                    var sourceIndex = Array.FindIndex(activeTargets, target => target.IsSource);
                    if (sourceIndex < 0 || results[sourceIndex] < expectedBytes)
                        throw new IOException($"Lectura incompleta del origen durante verificación: {entry.SourcePath}");

                    var crcStarted = Stopwatch.GetTimestamp();
                    var sourceCrc = FastCrc32C.Compute(activeTargets[sourceIndex].Buffer!.Memory.Span[..expectedBytes]);
                    job.Telemetry.RecordVerifyCrc32C(expectedBytes, Stopwatch.GetElapsedTime(crcStarted));

                    for (var index = 0; index < activeTargets.Length; index++)
                    {
                        var target = activeTargets[index];
                        if (target.IsSource)
                            continue;
                        if (results[index] < expectedBytes)
                        {
                            workers[target.Slot].Fail($"Lectura incompleta durante verificación: {target.Path}");
                            continue;
                        }

                        crcStarted = Stopwatch.GetTimestamp();
                        var destinationCrc = FastCrc32C.Compute(target.Buffer!.Memory.Span[..expectedBytes]);
                        job.Telemetry.RecordVerifyCrc32C(expectedBytes, Stopwatch.GetElapsedTime(crcStarted));
                        if (destinationCrc != sourceCrc)
                        {
                            workers[target.Slot].Fail($"CRC32C no coincide durante verificación: {target.Path}");
                            continue;
                        }
                        target.Progress!.AddVerified(expectedBytes);
                    }

                    // Source-equivalent progress: one logical chunk, regardless of destination count.
                    job.Telemetry.RecordVerifyLogicalBytes(expectedBytes);
                    offset = checked(offset + expectedBytes);
                }

                ValidateSourceSnapshot(entry);
                if (offset != entry.Size)
                {
                    foreach (var target in targets.Where(target => !target.IsSource && workers[target.Slot].IsActive))
                        workers[target.Slot].Fail($"Verificación incompleta: {target.Path}");
                }
                else
                {
                    foreach (var target in targets.Where(target => !target.IsSource && workers[target.Slot].IsActive))
                        target.Progress!.MarkVerifyFileDone();
                }
            }
            finally
            {
                foreach (var target in targets)
                    target.Dispose();
            }
        }
    }

    private static async Task<int> ReadVerifyTargetAsync(
        CoordinatedVerifyTarget target,
        int expectedBytes,
        long offset,
        CopyJob job)
    {
        var retryCount = 0;
        while (true)
        {
            job.Token.ThrowIfCancellationRequested();
            var requestBytes = target.Direct is null
                ? expectedBytes
                : AlignUp(expectedBytes, target.Direct.Alignment);

            IDisposable? io = null;
            if (target.Scheduler is not null)
                io = await target.Scheduler.AcquireIoAsync(requestBytes, job.Token).ConfigureAwait(false);
            using (io)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    int read;
                    if (target.Direct is not null)
                    {
                        try
                        {
                            read = await target.Direct.ReadAsync(target.Buffer!, requestBytes, offset, job.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                        {
                            target.SwitchToBuffered();
                            continue;
                        }
                    }
                    else
                    {
                        read = await RandomAccess.ReadAsync(
                            target.BufferedHandle!,
                            target.Buffer!.Memory[..expectedBytes],
                            offset,
                            job.Token).ConfigureAwait(false);
                    }

                    job.Telemetry.RecordVerifyRead(expectedBytes, Stopwatch.GetElapsedTime(started));
                    if (retryCount > 0)
                    {
                        job.Telemetry.RecordIoRecovery(
                            "verify-read", target.Path, target.Direct is null ? "buffered" : "direct", 0,
                            target.Scheduler?.CurrentQueueDepth ?? 1, retryCount, offset, recovered: true);
                    }
                    return read;
                }
                catch (Exception ex) when (TransientIoErrorClassifier.IsTransient(ex))
                {
                    retryCount++;
                    target.Progress?.AddRetry();
                    job.Telemetry.RecordIoRecovery(
                        "verify-read",
                        target.Path,
                        target.Direct is null ? "buffered" : "direct",
                        TransientIoErrorClassifier.GetNativeCodeOrZero(ex),
                        target.Scheduler?.CurrentQueueDepth ?? 1,
                        retryCount,
                        offset,
                        recovered: false);
                }
            }

            await DelayTransientRetryAsync(retryCount, job.Token).ConfigureAwait(false);
        }
    }

    private static Task DelayTransientRetryAsync(int retryCount, CancellationToken token)
    {
        // No arbitrary retry-count cutoff: recoverable USB/storage faults can settle.
        // A short capped delay prevents a disconnected device from becoming a hot spin;
        // the user can always cancel the job.
        var milliseconds = Math.Min(250, 10 * Math.Min(Math.Max(1, retryCount), 25));
        return Task.Delay(milliseconds, token);
    }

    private static int AlignUp(int value, int alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private sealed class VerificationWorkspace : IDisposable
    {
        private readonly byte[] _buffer;
        private readonly int _baseOffset;
        private readonly int _streamCount;
        private int _disposed;

        internal VerificationWorkspace(IReadOnlyList<CoordinatedVerifyTarget> targets)
        {
            ArgumentNullException.ThrowIfNull(targets);
            if (targets.Count <= 1)
                throw new ArgumentOutOfRangeException(nameof(targets));

            var alignment = targets
                .Select(target => Math.Max(Environment.SystemPageSize, DirectIoSourceReader.RequiredAlignment(target.Device)))
                .Max();
            var rawPerStream = VerificationWorkspaceBytes / targets.Count;
            PerStreamBytes = rawPerStream - rawPerStream % alignment;
            if (PerStreamBytes < alignment)
                throw new IOException("Demasiados destinos para el workspace fijo de verificación.");

            _streamCount = targets.Count;
            _buffer = GC.AllocateUninitializedArray<byte>(checked(VerificationWorkspaceBytes + alignment), pinned: true);
            var rawPointer = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, 0).ToInt64();
            var remainder = rawPointer % alignment;
            var alignedPointer = remainder == 0 ? rawPointer : checked(rawPointer + alignment - remainder);
            _baseOffset = checked((int)(alignedPointer - rawPointer));
        }

        internal int PerStreamBytes { get; }

        internal SourceBufferLease RentSlice(int index)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if ((uint)index >= (uint)_streamCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            var offset = checked(_baseOffset + index * PerStreamBytes);
            var pointer = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, offset);
            return SourceBufferLease.BorrowPinned(_buffer, offset, PerStreamBytes, pointer);
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class CoordinatedVerifyTarget : IDisposable
    {
        internal CoordinatedVerifyTarget(
            int slot,
            string path,
            StorageDeviceInfo device,
            DeviceScheduler? scheduler,
            DestinationProgress? progress)
        {
            Slot = slot;
            Path = path;
            Device = device;
            Scheduler = scheduler;
            Progress = progress;
        }

        internal bool IsSource => Slot < 0;
        internal int Slot { get; }
        internal string Path { get; }
        internal StorageDeviceInfo Device { get; }
        internal DeviceScheduler? Scheduler { get; }
        internal DestinationProgress? Progress { get; }
        internal DirectIoSourceReader.OverlappedSession? Direct { get; private set; }
        internal Microsoft.Win32.SafeHandles.SafeFileHandle? BufferedHandle { get; private set; }
        internal SourceBufferLease? Buffer { get; private set; }

        internal void Open(SourceBufferLease buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Buffer = buffer;
            if (DirectIoSourceReader.TryOpenOverlappedForVerification(Path, Device, buffer.Capacity, out var direct))
            {
                Direct = direct;
                return;
            }
            BufferedHandle = File.OpenHandle(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        internal void SwitchToBuffered()
        {
            Direct?.Dispose();
            Direct = null;
            BufferedHandle ??= File.OpenHandle(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        public void Dispose()
        {
            Direct?.Dispose();
            BufferedHandle?.Dispose();
            Buffer?.Dispose();
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
    private static int SelectSharedFanoutBlockSize(long fileSize, int alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        var desired = (int)Math.Min(SharedFanoutBlockBytes, Math.Max((long)alignment, fileSize));
        var remainder = desired % alignment;
        return remainder == 0 ? desired : checked(desired + alignment - remainder);
    }

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
        var options = FileOptions.SequentialScan;
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

    private static void DrainAndRelease(ChannelReader<FanoutMessage> reader, DestinationWorker worker)
    {
        while (reader.TryRead(out var message))
        {
            worker.DecrementQueueDepth();
            ReleaseQueuedPayload(worker, message);
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

    private abstract record FanoutMessage;
    private abstract record ControlMessage : FanoutMessage;
    private sealed record BeginMessage(FileEntry Entry) : ControlMessage;
    private sealed record DataMessage(FanoutBlock Block) : FanoutMessage;
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

    internal abstract class FanoutBlock
    {
        internal abstract int Length { get; }
        internal abstract ReadOnlyMemory<byte> Memory { get; }
        internal abstract bool IsAlignedFor(int alignment);
        internal abstract bool IsSpill { get; }
        internal abstract void Release();
    }

    private sealed class SpillBlock(FanoutSpillBlock block) : FanoutBlock
    {
        private FanoutSpillBlock? _block = block ?? throw new ArgumentNullException(nameof(block));
        internal override int Length => (_block ?? throw new ObjectDisposedException(nameof(SpillBlock))).Length;
        internal override ReadOnlyMemory<byte> Memory => (_block ?? throw new ObjectDisposedException(nameof(SpillBlock))).Memory;
        internal override bool IsAlignedFor(int alignment) => (_block ?? throw new ObjectDisposedException(nameof(SpillBlock))).IsAlignedFor(alignment);
        internal override bool IsSpill => true;
        internal override void Release() => Interlocked.Exchange(ref _block, null)?.Dispose();
    }

    internal sealed class SharedBlock : FanoutBlock
    {
        private SharedFanoutBufferPool.Lease? _lease;

        internal SharedBlock(SharedFanoutBufferPool.Lease lease, int length)
        {
            ArgumentNullException.ThrowIfNull(lease);
            if (length <= 0 || length > lease.Memory.Length)
                throw new ArgumentOutOfRangeException(nameof(length));
            _lease = lease;
            Length = length;
        }

        internal override int Length { get; }
        internal override ReadOnlyMemory<byte> Memory =>
            (_lease ?? throw new ObjectDisposedException(nameof(SharedBlock))).Memory[..Length];

        internal override bool IsAlignedFor(int alignment) =>
            (_lease ?? throw new ObjectDisposedException(nameof(SharedBlock))).IsAlignedFor(alignment);
        internal override bool IsSpill => false;

        internal override void Release()
        {
            var lease = Volatile.Read(ref _lease)
                ?? throw new InvalidOperationException("SharedBlock liberado más veces que referencias asignadas.");
            if (lease.ReleaseReference())
                Interlocked.CompareExchange(ref _lease, null, lease);
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

    private sealed class DestinationWorker
    {
        public HashSet<string> CompletedFiles { get; } = new(StringComparer.Ordinal);
        private int _active = 1;
        private int _queueDepth;
        private long _pendingPayloadBytes;
        private long _peakPendingPayloadBytes;
        private long _spillBytes;
        private long _peakSpillBytes;
        private long _lastProgressTicks = DateTime.UtcNow.Ticks;

        public DestinationWorker(
            string root,
            int slot,
            DestinationProgress progress,
            StorageDeviceInfo device,
            DeviceScheduler deviceScheduler,
            AdaptiveControlByteBudget controlBudget,
            FanoutSpillBudget spillBudget,
            int destinationCount)
        {
            Root = root;
            Slot = slot;
            Progress = progress;
            Device = device;
            DeviceScheduler = deviceScheduler;
            ControlBudget = controlBudget ?? throw new ArgumentNullException(nameof(controlBudget));
            SpillBudget = spillBudget ?? throw new ArgumentNullException(nameof(spillBudget));
            SpillCeilingBytes = spillBudget.DestinationCeiling(destinationCount, deviceScheduler.BacklogTargetBytes);
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
        internal FanoutSpillBudget SpillBudget { get; }
        internal FanoutSpillController SpillController { get; } = new();
        internal long SpillCeilingBytes { get; }
        internal long SpillBytes => Interlocked.Read(ref _spillBytes);
        internal long PeakSpillBytes => Interlocked.Read(ref _peakSpillBytes);
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
                if (observed == peak) break;
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

        internal bool TryReserveSpill(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            while (true)
            {
                var current = Interlocked.Read(ref _spillBytes);
                var next = checked(current + bytes);
                if (next > SpillCeilingBytes || !SpillBudget.TryReserve(bytes))
                    return false;
                if (Interlocked.CompareExchange(ref _spillBytes, next, current) == current)
                {
                    var peak = Interlocked.Read(ref _peakSpillBytes);
                    while (next > peak)
                    {
                        var observed = Interlocked.CompareExchange(ref _peakSpillBytes, next, peak);
                        if (observed == peak) break;
                        peak = observed;
                    }
                    return true;
                }
                SpillBudget.Release(bytes);
            }
        }

        internal void ReleaseSpill(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            while (true)
            {
                var current = Interlocked.Read(ref _spillBytes);
                if (current < bytes)
                    throw new InvalidOperationException("El destino intentó liberar más spill del reservado.");
                if (Interlocked.CompareExchange(ref _spillBytes, current - bytes, current) == current)
                    break;
            }
            SpillBudget.Release(bytes);
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
            SpillController.Fail();
            Progress.MarkError(error);
            Progress.SetPhase(DestinationPhase.Failed, error);
            Channel.Writer.TryComplete();
        }
    }

    private enum PendingWriteStatus
    {
        Success,
        NeedsBufferedRetry,
        Failed,
    }

    private sealed record PendingWriteResult(
        PendingWriteStatus Status,
        FanoutBlock? RetryBlock,
        long Offset,
        Exception? Error)
    {
        internal static PendingWriteResult Success() =>
            new(PendingWriteStatus.Success, null, 0, null);

        internal static PendingWriteResult NeedsBufferedRetry(FanoutBlock block, long offset, Exception error) =>
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
