using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private const int ReservedRam = 512 * 1024 * 1024;
    private const int MinQueue = 2;
    private const int MaxQueue = 16;
    private const int Retries = 2;

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
            preverifiedSkips);
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
        try
        {
            var skipMasks = options.SkipSame
                ? await BuildVerifiedSkipMasksAsync(copy, progress, job, token).ConfigureAwait(false)
                : CreateEmptySkipMasks(copy.Files.Count, copy.DestinationRoots.Length);
            for (var fileIndex = 0; fileIndex < copy.Files.Count; fileIndex++)
            {
                for (var slot = 0; slot < copy.DestinationRoots.Length; slot++)
                    skipMasks[fileIndex][slot] |= copy.PreverifiedSkips[fileIndex][slot];
            }

            var queueDepth = QueueDepthFor(copy.DestinationRoots.Length);
            workers = copy.DestinationRoots
                .Select((root, index) => new DestinationWorker(root, index, progress[index], queueDepth))
                .ToArray();

            var writerTasks = workers
                .Select(worker => Task.Run(() => WriterLoopAsync(worker, options, job, expectedHashes), CancellationToken.None))
                .ToArray();

            Exception? producerError = null;
            try
            {
                await ProducerLoopAsync(copy, workers, progress, skipMasks, expectedHashes, job).ConfigureAwait(false);
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
                await VerifyDestinationsAsync(copy, workers, progress, expectedHashes, job).ConfigureAwait(false);

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
        CopyJob job)
    {
        var token = job.Token;
        var bufferBudget = new SemaphoreSlim(Math.Max(8, ReservedRam / BlockSize));
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

                using var hasher = Hasher.New();
                await using var source = new FileStream(entry.SourcePath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    BufferSize = 1024 * 1024,
                });

                long totalRead = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    job.WaitIfPaused(token);
                    await bufferBudget.WaitAsync(token).ConfigureAwait(false);
                    var rented = ArrayPool<byte>.Shared.Rent(BlockSize);
                    int read;
                    try
                    {
                        read = await source.ReadAsync(rented.AsMemory(0, BlockSize), token).ConfigureAwait(false);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                        bufferBudget.Release();
                        throw;
                    }

                    if (read == 0)
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                        bufferBudget.Release();
                        break;
                    }

                    totalRead += read;
                    hasher.Update(rented.AsSpan(0, read));
                    var recipients = active.Where(worker => worker.IsActive).ToArray();
                    if (recipients.Length == 0)
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                        bufferBudget.Release();
                        break;
                    }

                    var block = new SharedBlock(rented, read, recipients.Length, bufferBudget);
                    await DeliverAsync(recipients, new DataMessage(block), countsData: true, job).ConfigureAwait(false);
                    active.RemoveAll(worker => !worker.IsActive);
                    if (active.Count == 0) break;
                }

                if (totalRead != entry.Size)
                    throw new IOException($"El origen cambió de tamaño durante la copia: {entry.RelativePath}");
                ValidateSourceSnapshot(entry);
                var hash = hasher.Finalize().AsSpan().ToArray();
                var key = PathKey(entry.RelativePath);
                if (expectedHashes.TryGetValue(key, out var preflightHash) && !hash.AsSpan().SequenceEqual(preflightHash))
                    throw new IOException($"El origen cambió durante la copia: {entry.RelativePath}");
                expectedHashes[key] = hash;
                await DeliverAsync(active.Where(worker => worker.IsActive).ToArray(), new EndMessage(hash), countsData: false, job).ConfigureAwait(false);
            }
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

    private static async Task DeliverAsync(
        IReadOnlyCollection<DestinationWorker> recipients,
        FanoutMessage message,
        bool countsData,
        CopyJob job)
    {
        var targets = recipients as DestinationWorker[] ?? recipients.ToArray();
        for (var index = 0; index < targets.Length; index++)
        {
            var worker = targets[index];
            if (!worker.IsActive)
            {
                ReleaseIfData(message);
                continue;
            }

            job.Token.ThrowIfCancellationRequested();
            job.WaitIfPaused(job.Token);
            if (countsData) worker.IncrementQueueDepth();
            try
            {
                await worker.Channel.Writer.WriteAsync(message, job.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (countsData) worker.DecrementQueueDepth();
                ReleaseIfData(message);
                ReleaseUndeliveredData(message, targets.Length - index - 1);
                throw;
            }
            catch (ChannelClosedException)
            {
                if (countsData) worker.DecrementQueueDepth();
                ReleaseIfData(message);
                if (worker.IsActive)
                    worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
            }
            catch
            {
                if (countsData) worker.DecrementQueueDepth();
                ReleaseIfData(message);
                ReleaseUndeliveredData(message, targets.Length - index - 1);
                throw;
            }
        }
    }

    private static async Task WriterLoopAsync(
        DestinationWorker worker,
        CopyOptions options,
        CopyJob job,
        ConcurrentDictionary<string, byte[]> expectedHashes)
    {
        CurrentFile? current = null;
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
                                await WriteWithRetryAsync(worker, current, chunkData.Block.Memory, job).ConfigureAwait(false);
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
                        await FinishFileAsync(worker, current, end.Hash, options, job).ConfigureAwait(false);
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
            BufferSize = 1024 * 1024,
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
                await current.Stream.WriteAsync(data, job.Token).ConfigureAwait(false);
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

    private static async Task FinishFileAsync(
        DestinationWorker worker,
        CurrentFile current,
        byte[] expectedHash,
        CopyOptions options,
        CopyJob job)
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
            await current.Stream.FlushAsync(job.Token).ConfigureAwait(false);
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
        AppendRecoveryState(worker.Root, current.Entry, expectedHash);
        worker.Progress.MarkDone();
    }

    private static async Task VerifyDestinationsAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        ConcurrentDictionary<string, byte[]> expectedHashes,
        CopyJob job)
    {
        for (var slot = 0; slot < workers.Length; slot++)
        {
            if (!workers[slot].IsActive) continue;
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
                var actual = await HashFileAsync(destination, job.Token).ConfigureAwait(false);
                if (!actual.AsSpan().SequenceEqual(expected))
                {
                    workers[slot].Fail($"BLAKE3 no coincide: {destination}");
                    break;
                }
            }
        }
    }

    private static async Task<bool[][]> BuildVerifiedSkipMasksAsync(
        PreparedCopy copy,
        DestinationProgress[] progress,
        CopyJob job,
        CancellationToken token)
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

            var sourceHash = await HashFileAsync(entry.SourcePath, token).ConfigureAwait(false);
            foreach (var slot in candidates)
            {
                var destination = Path.Combine(copy.DestinationRoots[slot], entry.RelativePath);
                var destinationHash = await HashFileAsync(destination, token).ConfigureAwait(false);
                if (destinationHash.AsSpan().SequenceEqual(sourceHash))
                {
                    masks[fileIndex][slot] = true;
                    progress[slot].SetLastFile(entry.RelativePath);
                }
            }
        }
        return masks;
    }

    private static bool[][] CreateEmptySkipMasks(int files, int destinations) =>
        Enumerable.Range(0, files).Select(_ => new bool[destinations]).ToArray();

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken token)
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
                BufferSize = 1024 * 1024,
            });
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read == 0) break;
                hasher.Update(buffer.AsSpan(0, read));
            }
            return hasher.Finalize().AsSpan().ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void AppendRecoveryState(string root, FileEntry entry, byte[] hash) =>
        RecoveryManager.AppendDurable(
            root,
            new RecoveryFile(
                entry.SourcePath,
                entry.RelativePath,
                entry.Size,
                entry.ModifiedUnixNanoseconds),
            hash);

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

    private static int QueueDepthFor(int destinations) =>
        destinations <= 0
            ? MinQueue
            : Math.Clamp(ReservedRam / (destinations * BlockSize), MinQueue, MaxQueue);

    private static string PathKey(string relative) => relative.Replace('/', '\\');

    private static long ToUnixNanoseconds(DateTime utc) =>
        checked((utc.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks) * 100L);

    private static FileStream ReopenPart(string path, long offset)
    {
        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1024 * 1024,
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

    private static void ReleaseUndeliveredData(FanoutMessage message, int count)
    {
        if (message is not DataMessage data) return;
        for (var index = 0; index < count; index++)
            data.Block.Release();
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
        bool[][] PreverifiedSkips);

    private sealed record FileEntry(
        string SourcePath,
        string RelativePath,
        long Size,
        DateTime LastWriteTimeUtc,
        long ModifiedUnixNanoseconds)
    ;

    private abstract record FanoutMessage;
    private sealed record BeginMessage(FileEntry Entry) : FanoutMessage;
    private sealed record DataMessage(SharedBlock Block) : FanoutMessage;
    private sealed record EndMessage(byte[] Hash) : FanoutMessage;

    private sealed class SharedBlock
    {
        private byte[]? _buffer;
        private int _references;
        private readonly SemaphoreSlim _budget;

        public SharedBlock(byte[] buffer, int length, int references, SemaphoreSlim budget)
        {
            _buffer = buffer;
            Length = length;
            _references = references;
            _budget = budget;
        }

        public int Length { get; }
        public ReadOnlyMemory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(SharedBlock))).AsMemory(0, Length);

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
            _budget.Release();
        }
    }

    private sealed class DestinationWorker
    {
        private int _active = 1;
        private int _queueDepth;
        private long _lastProgressTicks = DateTime.UtcNow.Ticks;

        public DestinationWorker(string root, int slot, DestinationProgress progress, int capacity)
        {
            Root = root;
            Slot = slot;
            Progress = progress;
            Channel = System.Threading.Channels.Channel.CreateBounded<FanoutMessage>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
        }

        public string Root { get; }
        public int Slot { get; }
        public DestinationProgress Progress { get; }
        public Channel<FanoutMessage> Channel { get; }
        public bool IsActive => Volatile.Read(ref _active) != 0;
        public DateTime LastProgressUtc => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);

        public void NoteProgress() => Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);

        public void IncrementQueueDepth()
        {
            var depth = Interlocked.Increment(ref _queueDepth);
            Progress.SetQueueDepth(depth);
        }

        public void DecrementQueueDepth()
        {
            var depth = Math.Max(0, Interlocked.Decrement(ref _queueDepth));
            Progress.SetQueueDepth(depth);
        }

        public void Fail(string error)
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            Progress.MarkError(error);
            Progress.SetPhase(DestinationPhase.Failed, error);
            Channel.Writer.TryComplete();
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

