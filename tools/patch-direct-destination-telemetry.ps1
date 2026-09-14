$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New, [string]$Label) {
    $text = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) { throw "${Label}: patrón exacto no encontrado" }
    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$copy = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$telemetry = 'dotnet/RepartoCopier.Core/CopyTelemetry.cs'
$reader = 'dotnet/RepartoCopier.Core/DirectIoSourceReader.cs'

Replace-Exact $copy @'
                            current = BeginFile(worker, begin.Entry);
                            job.Telemetry.RecordFilePolicy(current.WriteThrough);
                            break;
'@ @'
                            current = BeginFile(worker, begin.Entry);
                            job.Telemetry.RecordFilePolicy(current.WriteThrough);
                            if (current.DirectSession is not null)
                                job.Telemetry.RecordDirectDestinationFile();
                            else if (current.DirectRequested)
                                job.Telemetry.RecordDirectDestinationFallback();
                            break;
'@ 'telemetría de selección Direct por archivo'

Replace-Exact $copy @'
                            operations = await current.DirectSession.WriteAsync(
                                data,
                                current.Copied,
                                current.Entry.Size,
                                payloadIsAligned: true,
                                queueDepth,
                                StorageWritePolicy.MinimumParallelSliceBytes,
                                worker.DeviceScheduler,
                                job.Token).ConfigureAwait(false);
'@ @'
                            operations = await current.DirectSession.WriteAsync(
                                data,
                                current.Copied,
                                current.Entry.Size,
                                payloadIsAligned: true,
                                queueDepth,
                                StorageWritePolicy.MinimumParallelSliceBytes,
                                worker.DeviceScheduler,
                                job.Token).ConfigureAwait(false);
                            job.Telemetry.RecordDirectDestinationWrite(data.Length, operations);
'@ 'telemetría de bytes/ops Direct'

Replace-Exact $copy @'
                        SwitchToBuffered(current);
'@ @'
                        SwitchToBuffered(current, job);
'@ 'fallback por payload no alineado'

Replace-Exact $copy @'
                            SwitchToBuffered(current);
'@ @'
                            SwitchToBuffered(current, job);
'@ 'fallback por error compatible'

Replace-Exact $copy @'
    private static void SwitchToBuffered(CurrentFile current)
    {
        current.DirectSession?.Dispose();
        current.DirectSession = null;
        ResetPartLength(current.PartPath, current.Copied);
        current.Stream = ReopenPart(current.PartPath, current.Copied, current.WriteThrough);
    }
'@ @'
    private static void SwitchToBuffered(CurrentFile current, CopyJob job)
    {
        current.DirectSession?.Dispose();
        current.DirectSession = null;
        current.DirectEnabled = false;
        job.Telemetry.RecordDirectDestinationFallback();
        ResetPartLength(current.PartPath, current.Copied);
        current.Stream = ReopenPart(current.PartPath, current.Copied, current.WriteThrough);
    }
'@ 'hacer fallback sticky por archivo'

Replace-Exact $copy @'
                    if (current.PreferDirect)
                    {
                        if (!DirectIoDestinationWriter.TryOpen(current.PartPath, worker.Device, current.Entry.Size, out var reopened))
                            throw new IOException($"No se pudo reabrir Direct I/O para {current.Entry.RelativePath} durante reintento.", ex);
                        current.DirectSession = reopened;
                    }
'@ @'
                    if (current.DirectEnabled)
                    {
                        if (!DirectIoDestinationWriter.TryOpen(current.PartPath, worker.Device, current.Entry.Size, out var reopened))
                            throw new IOException($"No se pudo reabrir Direct I/O para {current.Entry.RelativePath} durante reintento.", ex);
                        current.DirectSession = reopened;
                    }
'@ 'reintentar Direct solo si sigue habilitado'

Replace-Exact $copy @'
        bool writeThrough,
        bool preferDirect)
'@ @'
        bool writeThrough,
        bool directRequested)
'@ 'renombrar intención Direct'

Replace-Exact $copy @'
        public bool WriteThrough { get; } = writeThrough;
        public bool PreferDirect { get; } = preferDirect;
        public long Copied { get; set; }
'@ @'
        public bool WriteThrough { get; } = writeThrough;
        public bool DirectRequested { get; } = directRequested;
        public bool DirectEnabled { get; set; } = directSession is not null;
        public long Copied { get; set; }
'@ 'persistir estado Direct por archivo'

Replace-Exact $telemetry @'
    public long DirectSourceReadBytes { get; init; }
    public long DirectSourceReadOperations { get; init; }
    public int DirectSourceFallbacks { get; init; }
'@ @'
    public long DirectSourceReadBytes { get; init; }
    public long DirectSourceReadOperations { get; init; }
    public int DirectSourceFallbacks { get; init; }
    public int DirectDestinationFiles { get; init; }
    public long DirectDestinationWriteBytes { get; init; }
    public long DirectDestinationWriteOperations { get; init; }
    public int DirectDestinationFallbacks { get; init; }
'@ 'extender snapshot Direct destination'

Replace-Exact $telemetry @'
    private long _directSourceReadBytes, _directSourceReadOperations;
    private int _directSourceFallbacks;
'@ @'
    private long _directSourceReadBytes, _directSourceReadOperations;
    private int _directSourceFallbacks;
    private int _directDestinationFiles, _directDestinationFallbacks;
    private long _directDestinationWriteBytes, _directDestinationWriteOperations;
'@ 'contadores internos Direct destination'

Replace-Exact $telemetry @'
    internal void RecordDirectSourceRead(int bytes) { AddBytes(ref _directSourceReadBytes, bytes); Interlocked.Increment(ref _directSourceReadOperations); }
    internal void RecordDirectSourceFallback() => Interlocked.Increment(ref _directSourceFallbacks);
'@ @'
    internal void RecordDirectSourceRead(int bytes) { AddBytes(ref _directSourceReadBytes, bytes); Interlocked.Increment(ref _directSourceReadOperations); }
    internal void RecordDirectSourceFallback() => Interlocked.Increment(ref _directSourceFallbacks);
    internal void RecordDirectDestinationFile() => Interlocked.Increment(ref _directDestinationFiles);
    internal void RecordDirectDestinationWrite(int bytes, int operations)
    {
        AddBytes(ref _directDestinationWriteBytes, bytes);
        if (operations > 0) Interlocked.Add(ref _directDestinationWriteOperations, operations);
    }
    internal void RecordDirectDestinationFallback() => Interlocked.Increment(ref _directDestinationFallbacks);
'@ 'métodos de telemetría Direct destination'

Replace-Exact $telemetry @'
            DirectSourceReadBytes = Interlocked.Read(ref _directSourceReadBytes),
            DirectSourceReadOperations = Interlocked.Read(ref _directSourceReadOperations),
            DirectSourceFallbacks = Volatile.Read(ref _directSourceFallbacks),
'@ @'
            DirectSourceReadBytes = Interlocked.Read(ref _directSourceReadBytes),
            DirectSourceReadOperations = Interlocked.Read(ref _directSourceReadOperations),
            DirectSourceFallbacks = Volatile.Read(ref _directSourceFallbacks),
            DirectDestinationFiles = Volatile.Read(ref _directDestinationFiles),
            DirectDestinationWriteBytes = Interlocked.Read(ref _directDestinationWriteBytes),
            DirectDestinationWriteOperations = Interlocked.Read(ref _directDestinationWriteOperations),
            DirectDestinationFallbacks = Volatile.Read(ref _directDestinationFallbacks),
'@ 'publicar telemetría Direct destination'

Replace-Exact $reader @'
        out OverlappedSession? session) =>
        TryOpenOverlappedCore(path, device, transferSize, verification: false, out session);

    internal static bool TryOpenOverlappedForVerification(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        out OverlappedSession? session) =>
        TryOpenOverlappedCore(path, device, transferSize, verification: true, out session);

    private static bool TryOpenOverlappedCore(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        bool verification,
        out OverlappedSession? session)
    {
        session = null;
        var eligible = verification
            ? IsVerificationEligible(device, transferSize)
            : IsEligible(device, transferSize);
        if (!eligible)
'@ @'
        out OverlappedSession? session) =>
        TryOpenOverlappedCore(path, device, transferSize, out session);

    internal static bool TryOpenOverlappedForVerification(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        out OverlappedSession? session) =>
        TryOpenOverlappedCore(path, device, transferSize, out session);

    private static bool TryOpenOverlappedCore(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        out OverlappedSession? session)
    {
        session = null;
        if (!IsEligibleCore(device, transferSize))
'@ 'eliminar parámetro verification redundante'

$copyText = [IO.File]::ReadAllText($copy)
if ($copyText.Contains('PreferDirect')) { throw 'Quedó estado PreferDirect obsoleto' }
if (-not $copyText.Contains('RecordDirectDestinationWrite')) { throw 'Telemetría Direct Write no quedó en hot path' }
if (-not $copyText.Contains('DirectEnabled = false')) { throw 'Fallback Direct no quedó sticky' }

$telemetryText = [IO.File]::ReadAllText($telemetry)
foreach ($required in @('DirectDestinationFiles', 'DirectDestinationWriteBytes', 'DirectDestinationWriteOperations', 'DirectDestinationFallbacks')) {
    if (-not $telemetryText.Contains($required)) { throw "Falta telemetría: $required" }
}

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $copy $telemetry $reader
if (git diff --cached --quiet) { throw 'El cierre de telemetría Direct no produjo cambios.' }
git commit -m 'perf(core): close direct destination fallback and telemetry path'
git push origin HEAD:main
