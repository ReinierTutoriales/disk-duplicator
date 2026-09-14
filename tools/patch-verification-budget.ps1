$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New, [string]$Label) {
    $text = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) { throw "${Label}: patrón exacto no encontrado" }
    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$copy = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$telemetry = 'dotnet/RepartoCopier.Core/CopyTelemetry.cs'

Replace-Exact $copy @'
    {
        var activeSlots = Enumerable.Range(0, workers.Length)
'@ @'
    {
        var readBudget = VerificationReadBudget.CreateForSystem();
        var activeSlots = Enumerable.Range(0, workers.Length)
'@ 'crear budget global de verificación'

Replace-Exact $copy @'
                    workers[slot].DeviceScheduler,
                    plan,
                    job,
                    progress[slot]).ConfigureAwait(false);
'@ @'
                    workers[slot].DeviceScheduler,
                    plan,
                    readBudget,
                    job,
                    progress[slot]).ConfigureAwait(false);
'@ 'conectar budget al lector real'

Replace-Exact $copy @'
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<bool[][]> BuildVerifiedSkipMasksAsync(
'@ @'
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
'@ 'publicar budget aunque Verify falle'

Replace-Exact $telemetry @'
    public int DirectDestinationFallbacks { get; init; }

    private static WritePolicyDiagnosticsSnapshot EmptyWritePolicy =>
'@ @'
    public int DirectDestinationFallbacks { get; init; }
    public long VerificationReadBudgetBytes { get; init; }
    public long PeakVerificationReadBytes { get; init; }

    private static WritePolicyDiagnosticsSnapshot EmptyWritePolicy =>
'@ 'campos snapshot budget verify'

Replace-Exact $telemetry @'
    private long _verifyReadBytes, _verifyReadTicks;
    private long _verifyHashBytes, _verifyHashTicks;
'@ @'
    private long _verifyReadBytes, _verifyReadTicks;
    private long _verifyHashBytes, _verifyHashTicks;
    private long _verificationReadBudgetBytes, _peakVerificationReadBytes;
'@ 'contadores budget verify'

Replace-Exact $telemetry @'
    internal void RecordVerifyRead(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyReadBytes, bytes); AddTicks(ref _verifyReadTicks, elapsed); }
    internal void RecordVerifyHash(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyHashBytes, bytes); AddTicks(ref _verifyHashTicks, elapsed); }
'@ @'
    internal void RecordVerifyRead(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyReadBytes, bytes); AddTicks(ref _verifyReadTicks, elapsed); }
    internal void RecordVerifyHash(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyHashBytes, bytes); AddTicks(ref _verifyHashTicks, elapsed); }
    internal void RecordVerificationBufferBudget(long budgetBytes, long peakBytes)
    {
        if (budgetBytes > 0) Interlocked.Exchange(ref _verificationReadBudgetBytes, budgetBytes);
        if (peakBytes > 0) UpdateMax(ref _peakVerificationReadBytes, peakBytes);
    }
'@ 'registrar budget verify'

Replace-Exact $telemetry @'
            DirectDestinationWriteOperations = Interlocked.Read(ref _directDestinationWriteOperations),
            DirectDestinationFallbacks = Volatile.Read(ref _directDestinationFallbacks),
'@ @'
            DirectDestinationWriteOperations = Interlocked.Read(ref _directDestinationWriteOperations),
            DirectDestinationFallbacks = Volatile.Read(ref _directDestinationFallbacks),
            VerificationReadBudgetBytes = Interlocked.Read(ref _verificationReadBudgetBytes),
            PeakVerificationReadBytes = Interlocked.Read(ref _peakVerificationReadBytes),
'@ 'publicar budget verify'

$copyText = [IO.File]::ReadAllText($copy)
if (-not $copyText.Contains('VerificationReadBudget.CreateForSystem()')) { throw 'CopyEngine no crea budget compartido' }
if (-not $copyText.Contains('readBudget,')) { throw 'FastVerificationReader no consume budget de producción' }
$telemetryText = [IO.File]::ReadAllText($telemetry)
if (-not $telemetryText.Contains('PeakVerificationReadBytes')) { throw 'Telemetría de budget no quedó conectada' }

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $copy $telemetry
if (git diff --cached --quiet) { throw 'La integración del budget Verify no produjo cambios.' }
git commit -m 'perf(core): integrate global verification byte budget'
git push origin HEAD:main
