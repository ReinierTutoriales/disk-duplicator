$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New, [string]$Label) {
    $text = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) { throw "$Label: patrón exacto no encontrado" }
    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$copy = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$reader = 'dotnet/RepartoCopier.Core/DirectIoSourceReader.cs'

Replace-Exact $reader @'
    internal static bool IsEligible(StorageDeviceInfo device, int transferSize) =>
        IsEligibleCore(device, transferSize, solidStateOnly: true);

    internal static bool IsVerificationEligible(StorageDeviceInfo device, int transferSize) =>
        IsEligibleCore(device, transferSize, solidStateOnly: false);

    private static bool IsEligibleCore(StorageDeviceInfo device, int transferSize, bool solidStateOnly)
'@ @'
    internal const int MaximumSupportedAlignment = 64 * 1024;

    internal static bool IsEligible(StorageDeviceInfo device, int transferSize) =>
        IsEligibleCore(device, transferSize);

    internal static bool IsVerificationEligible(StorageDeviceInfo device, int transferSize) =>
        IsEligibleCore(device, transferSize);

    private static bool IsEligibleCore(StorageDeviceInfo device, int transferSize)
'@ 'unificar elegibilidad Direct I/O de origen'

Replace-Exact $reader @'
        if (solidStateOnly && device.MediaKind != StorageMediaKind.SolidState)
            return false;
        if (!device.HasKnownSectorAlignment)
'@ @'
        if (!device.HasKnownSectorAlignment)
'@ 'eliminar restricción SSD-only'

Replace-Exact $reader @'
        return alignment is >= 512 and <= 64 * 1024 &&
'@ @'
        return alignment is >= 512 and <= MaximumSupportedAlignment &&
'@ 'centralizar alineación máxima'

Replace-Exact $reader @'
    internal bool IsPinned => _pinned && _pin.IsAllocated;
    internal int Capacity => _capacity;
'@ @'
    internal bool IsPinned => _pinned && _pin.IsAllocated;
    internal int Capacity => _capacity;

    internal bool IsAlignedFor(int alignment)
    {
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));
        return IsPinned && Pointer.ToInt64() % alignment == 0;
    }
'@ 'exponer contrato de alineación del payload'

Replace-Exact $copy @'
                    lease = direct is null
                        ? SourceBufferLease.RentBuffered(readBufferSize)
                        : SourceBufferLease.RentAligned(readBufferSize, direct.Alignment);
'@ @'
                    lease = direct is null
                        ? SourceBufferLease.RentBuffered(readBufferSize)
                        : SourceBufferLease.RentAligned(readBufferSize, DirectIoSourceReader.MaximumSupportedAlignment);
'@ 'sobre-alinear buffer directo del origen'

Replace-Exact $copy @'
                                    await WriteWithRetryAsync(worker, current, chunkData.Block.Memory, job).ConfigureAwait(false);
'@ @'
                                    await WriteWithRetryAsync(worker, current, chunkData.Block, job).ConfigureAwait(false);
'@ 'pasar SharedBlock al writer'

Replace-Exact $copy @'
                                current.Stream?.Dispose();
                                current.Stream = null;
                                TryDelete(current.PartPath);
'@ @'
                                current.Stream?.Dispose();
                                current.Stream = null;
                                current.DirectSession?.Dispose();
                                current.DirectSession = null;
                                TryDelete(current.PartPath);
'@ 'cerrar sesión directa en error de archivo'

Replace-Exact $copy @'
                current.Stream?.Dispose();
                TryDelete(current.PartPath);
'@ @'
                current.Stream?.Dispose();
                current.DirectSession?.Dispose();
                TryDelete(current.PartPath);
'@ 'cerrar sesión directa en finally'

Replace-Exact $copy @'
        var writeThrough = entry.Size <= WriteThroughFileThreshold;
        var stream = OpenPartStream(
            part,
            FileMode.CreateNew,
            offset: 0,
            writeThrough,
            StoragePreallocationPolicy.GetPreallocationSize(part, entry.Size, PreallocationThreshold));
        return new CurrentFile(entry, destination, part, transient.BackupPath, stream, writeThrough);
'@ @'
        var writeThrough = entry.Size <= WriteThroughFileThreshold;
        var preallocationSize = StoragePreallocationPolicy.GetPreallocationSize(part, entry.Size, PreallocationThreshold);
        FileStream? stream = null;
        DirectIoDestinationWriter.Session? directSession = null;
        var preferDirect = !writeThrough && DirectIoDestinationWriter.IsEligible(worker.Device, entry.Size);
        if (preferDirect)
        {
            using (OpenPartStream(part, FileMode.CreateNew, offset: 0, writeThrough: false, preallocationSize)) { }
            if (!DirectIoDestinationWriter.TryOpen(part, worker.Device, entry.Size, out directSession))
                stream = ReopenPart(part, 0, writeThrough: false);
        }
        else
        {
            stream = OpenPartStream(part, FileMode.CreateNew, offset: 0, writeThrough, preallocationSize);
        }
        return new CurrentFile(entry, destination, part, transient.BackupPath, stream, directSession, writeThrough, preferDirect);
'@ 'seleccionar una sola estrategia de escritura por archivo'

$oldWrite = @'
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
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
            try
            {
                current.Stream ??= ReopenPart(current.PartPath, current.Copied, current.WriteThrough);
                var queueDepth = StorageWritePolicy.LargeWriteQueueDepth(
                    worker.Device,
                    worker.DeviceScheduler.MaxOutstandingIo,
                    current.Entry.Size,
                    data.Length);
                var started = Stopwatch.GetTimestamp();
                var operations = await DestinationWriteCoordinator.WriteAsync(
                    current.Stream.SafeFileHandle,
                    data,
                    current.Copied,
                    queueDepth,
                    StorageWritePolicy.MinimumParallelSliceBytes,
                    worker.DeviceScheduler,
                    job.Token).ConfigureAwait(false);
                for (var operation = 0; operation < operations; operation++)
                    job.Telemetry.RecordWriteOperation();
                job.Telemetry.RecordWrite(data.Length, Stopwatch.GetElapsedTime(started), current.WriteThrough);
                worker.NoteProgress();
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
'@
$newWrite = @'
    private static async Task WriteWithRetryAsync(
        DestinationWorker worker,
        CurrentFile current,
        SharedBlock block,
        CopyJob job)
    {
        var data = block.Memory;
        Exception? last = null;
        for (var attempt = 0; attempt <= Retries; attempt++)
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
            try
            {
                var queueDepth = StorageWritePolicy.LargeWriteQueueDepth(
                    worker.Device,
                    worker.DeviceScheduler.MaxOutstandingIo,
                    current.Entry.Size,
                    data.Length);
                var started = Stopwatch.GetTimestamp();
                var operations = 0;

                if (current.DirectSession is not null)
                {
                    if (!block.IsAlignedFor(current.DirectSession.Alignment))
                    {
                        SwitchToBuffered(current);
                    }
                    else
                    {
                        try
                        {
                            operations = await current.DirectSession.WriteAsync(
                                data,
                                current.Copied,
                                current.Entry.Size,
                                payloadIsAligned: true,
                                queueDepth,
                                StorageWritePolicy.MinimumParallelSliceBytes,
                                worker.DeviceScheduler,
                                job.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (DirectIoDestinationWriter.IsFallbackable(ex))
                        {
                            SwitchToBuffered(current);
                        }
                    }
                }

                if (current.DirectSession is null)
                {
                    current.Stream ??= ReopenPart(current.PartPath, current.Copied, current.WriteThrough);
                    operations = await DestinationWriteCoordinator.WriteAsync(
                        current.Stream.SafeFileHandle,
                        data,
                        current.Copied,
                        queueDepth,
                        StorageWritePolicy.MinimumParallelSliceBytes,
                        worker.DeviceScheduler,
                        job.Token).ConfigureAwait(false);
                }

                for (var operation = 0; operation < operations; operation++)
                    job.Telemetry.RecordWriteOperation();
                job.Telemetry.RecordWrite(data.Length, Stopwatch.GetElapsedTime(started), current.WriteThrough);
                worker.NoteProgress();
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                current.Stream?.Dispose();
                current.Stream = null;
                current.DirectSession?.Dispose();
                current.DirectSession = null;
                ResetPartLength(current.PartPath, current.Copied);
                if (attempt < Retries)
                {
                    worker.Progress.AddRetry();
                    await Task.Delay(75 * (attempt + 1), job.Token).ConfigureAwait(false);
                    if (current.PreferDirect)
                    {
                        if (!DirectIoDestinationWriter.TryOpen(current.PartPath, worker.Device, current.Entry.Size, out var reopened))
                            throw new IOException($"No se pudo reabrir Direct I/O para {current.Entry.RelativePath} durante reintento.", ex);
                        current.DirectSession = reopened;
                    }
                }
            }
        }
        throw new IOException($"No se pudo escribir {current.Entry.RelativePath} después de reintentos.", last);
    }

    private static void SwitchToBuffered(CurrentFile current)
    {
        current.DirectSession?.Dispose();
        current.DirectSession = null;
        ResetPartLength(current.PartPath, current.Copied);
        current.Stream = ReopenPart(current.PartPath, current.Copied, current.WriteThrough);
    }

    private static void ResetPartLength(string path, long length)
    {
        using var reset = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        reset.SetLength(length);
        reset.Flush(flushToDisk: true);
    }
'@
Replace-Exact $copy $oldWrite $newWrite 'migrar writer de producción a sesión Direct/Buffered única'

Replace-Exact $copy @'
            current.Stream?.Dispose();
            current.Stream = null;
            TryDelete(current.PartPath);
'@ @'
            current.Stream?.Dispose();
            current.Stream = null;
            current.DirectSession?.Dispose();
            current.DirectSession = null;
            TryDelete(current.PartPath);
'@ 'cerrar direct en tamaño inesperado'

Replace-Exact $copy @'
        if (current.Stream is not null)
        {
            if (!current.WriteThrough)
            {
                var flushStarted = Stopwatch.GetTimestamp();
                current.Stream.Flush(flushToDisk: true);
                job.Telemetry.RecordFlush(Stopwatch.GetElapsedTime(flushStarted));
            }
            current.Stream.Dispose();
            current.Stream = null;
        }
'@ @'
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
            if (!current.WriteThrough)
            {
                var flushStarted = Stopwatch.GetTimestamp();
                current.Stream.Flush(flushToDisk: true);
                job.Telemetry.RecordFlush(Stopwatch.GetElapsedTime(flushStarted));
            }
            current.Stream.Dispose();
            current.Stream = null;
        }
'@ 'finalizar EOF exacto y durabilidad Direct I/O'

Replace-Exact $copy @'
        public int Length { get; }
        public uint VerificationCrc32 { get; }
        public ReadOnlyMemory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(SharedBlock))).Memory[..Length];

        public void Release()
'@ @'
        public int Length { get; }
        public uint VerificationCrc32 { get; }
        public ReadOnlyMemory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(SharedBlock))).Memory[..Length];
        internal bool IsAlignedFor(int alignment) =>
            (_buffer ?? throw new ObjectDisposedException(nameof(SharedBlock))).IsAlignedFor(alignment);

        public void Release()
'@ 'exponer alineación del SharedBlock'

Replace-Exact $copy @'
    private sealed class CurrentFile(
        FileEntry entry,
        string destinationPath,
        string partPath,
        string backupPath,
        FileStream stream,
        bool writeThrough)
    {
        public List<VerificationBlock> VerificationBlocks { get; } = [];
        public FileEntry Entry { get; } = entry;
        public string DestinationPath { get; } = destinationPath;
        public string PartPath { get; } = partPath;
        public string BackupPath { get; } = backupPath;
        public FileStream? Stream { get; set; } = stream;
        public bool WriteThrough { get; } = writeThrough;
        public long Copied { get; set; }
        public bool Failed { get; set; }
    }
'@ @'
    private sealed class CurrentFile(
        FileEntry entry,
        string destinationPath,
        string partPath,
        string backupPath,
        FileStream? stream,
        DirectIoDestinationWriter.Session? directSession,
        bool writeThrough,
        bool preferDirect)
    {
        public List<VerificationBlock> VerificationBlocks { get; } = [];
        public FileEntry Entry { get; } = entry;
        public string DestinationPath { get; } = destinationPath;
        public string PartPath { get; } = partPath;
        public string BackupPath { get; } = backupPath;
        public FileStream? Stream { get; set; } = stream;
        public DirectIoDestinationWriter.Session? DirectSession { get; set; } = directSession;
        public bool WriteThrough { get; } = writeThrough;
        public bool PreferDirect { get; } = preferDirect;
        public long Copied { get; set; }
        public bool Failed { get; set; }
    }
'@ 'migrar CurrentFile a estrategia única activa'

$copyText = [IO.File]::ReadAllText($copy)
foreach ($old in @('WriteQueueDepthTwoAsync', 'AcquireIoPairAsync', 'WriteTwoAsync')) {
    if ($copyText.Contains($old)) { throw "Ruta antigua reapareció en CopyEngine: $old" }
}
if (-not $copyText.Contains('DirectIoDestinationWriter.TryOpen')) { throw 'Direct writer no quedó conectado a BeginFile/retry' }
if (-not $copyText.Contains('current.DirectSession.WriteAsync')) { throw 'Direct writer no quedó conectado al hot path' }
if (-not $copyText.Contains('DirectIoSourceReader.MaximumSupportedAlignment')) { throw 'El payload de origen no quedó sobre-alineado' }

$readerText = [IO.File]::ReadAllText($reader)
if ($readerText.Contains('solidStateOnly')) { throw 'Quedó restricción SSD-only en DirectIoSourceReader' }

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $copy $reader
if (git diff --cached --quiet) { throw 'La migración Direct I/O no produjo cambios.' }
git commit -m 'perf(core): integrate direct overlapped destination writes'
git push origin HEAD:main
