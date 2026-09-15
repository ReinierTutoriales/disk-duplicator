$ErrorActionPreference = 'Stop'

function Read-Text([string]$Path) {
    return [System.IO.File]::ReadAllText((Resolve-Path $Path))
}

function Write-Text([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText((Resolve-Path $Path), $Content, [System.Text.UTF8Encoding]::new($false))
}

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $content = Read-Text $Path
    if (-not $content.Contains($Old)) { throw "Expected text not found in $Path`n$Old" }
    $content = $content.Replace($Old, $New)
    Write-Text $Path $content
}

Write-Host '=== Pre-change architecture references ==='
git grep -n -E 'ExplicitOffsetWriter|FanoutPerformancePolicy|FastCrc32|VerificationCrc32|VerifyHash|RecordVerifyHash' -- dotnet docs README.md TESTING.md CHANGELOG.md 2>$null | Write-Host

# 1. Remove obsolete ExplicitOffsetWriter indirection. DestinationWriteCoordinator owns the single write primitive.
$coordinator = @'
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Single production primitive for one logical destination block. Queue depth is
/// supplied by independent in-flight blocks through DeviceScheduler; a sequential
/// block is never fragmented merely to manufacture concurrency.
/// </summary>
internal static class DestinationWriteCoordinator
{
    internal static async Task<int> WriteAsync(
        SafeFileHandle handle,
        ReadOnlyMemory<byte> data,
        long offset,
        DeviceScheduler scheduler,
        CancellationToken token,
        int alignment = 1)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(scheduler);
        if (data.IsEmpty)
            return 0;
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (alignment <= 0 || offset % alignment != 0 || data.Length % alignment != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));

        using var lease = await scheduler.AcquireIoAsync(data.Length, token).ConfigureAwait(false);
        await RandomAccess.WriteAsync(handle, data, offset, token).ConfigureAwait(false);
        return 1;
    }
}
'@
Write-Text 'dotnet/RepartoCopier.Core/DestinationWriteCoordinator.cs' $coordinator
git rm dotnet/RepartoCopier.Core/ExplicitOffsetWriter.cs
if (Test-Path 'dotnet/RepartoCopier.Core.Tests/ExplicitOffsetWriterTests.cs') {
    git mv dotnet/RepartoCopier.Core.Tests/ExplicitOffsetWriterTests.cs dotnet/RepartoCopier.Core.Tests/DestinationWriteCoordinatorTests.cs
    Replace-Exact 'dotnet/RepartoCopier.Core.Tests/DestinationWriteCoordinatorTests.cs' 'public sealed class ExplicitOffsetWriterTests' 'public sealed class DestinationWriteCoordinatorTests'
}

# 2. Unify storage profile policy: one type, one productive route.
$storageProfile = @'
namespace RepartoCopier.Core;

public sealed record StorageIoProfile(
    StorageProfileKind Kind,
    int InitialQueueDepth,
    int SoftBacklogWatermarkBytes,
    DeviceIdentityConfidence IdentityConfidence)
{
    private const int BacklogPerQueueSlotBytes = 8 * 1024 * 1024;

    public int QueueDepth => InitialQueueDepth;
    public int BacklogTargetBytes => SoftBacklogWatermarkBytes;

    public static StorageIoProfile For(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var confidence = DeviceIdentity.Classify(device);
        var kind = Classify(device, confidence);
        var initialQueueDepth = InitialQueueDepthFor(kind, confidence);
        var softBacklogWatermark = checked(BacklogPerQueueSlotBytes * Math.Max(1, initialQueueDepth));
        return new StorageIoProfile(kind, initialQueueDepth, softBacklogWatermark, confidence);
    }

    private static StorageProfileKind Classify(
        StorageDeviceInfo device,
        DeviceIdentityConfidence confidence)
    {
        if (device.IsNetwork)
            return StorageProfileKind.Network;
        if (device.MediaKind is StorageMediaKind.Rotational)
            return StorageProfileKind.Rotational;

        var bus = (device.BusType ?? string.Empty).Trim().ToUpperInvariant();
        var solid = device.MediaKind is StorageMediaKind.SolidState;
        if (bus.Contains("NVME", StringComparison.Ordinal))
            return StorageProfileKind.Nvme;
        if (bus.Contains("USB", StringComparison.Ordinal) && solid)
            return StorageProfileKind.UsbSsd;
        if ((bus.Contains("SATA", StringComparison.Ordinal) || bus.Contains("ATA", StringComparison.Ordinal)) && solid)
            return StorageProfileKind.SataSsd;
        if (solid && confidence is DeviceIdentityConfidence.ExactPhysical)
            return StorageProfileKind.SataSsd;
        return StorageProfileKind.Unknown;
    }

    private static int InitialQueueDepthFor(
        StorageProfileKind kind,
        DeviceIdentityConfidence confidence) => kind switch
        {
            StorageProfileKind.Network => 1,
            StorageProfileKind.Rotational => 1,
            StorageProfileKind.UsbSsd when confidence is DeviceIdentityConfidence.ExactPhysical => 4,
            StorageProfileKind.UsbSsd => 1,
            StorageProfileKind.SataSsd when confidence is DeviceIdentityConfidence.ExactPhysical => 8,
            StorageProfileKind.SataSsd => 2,
            StorageProfileKind.Nvme when confidence is DeviceIdentityConfidence.ExactPhysical => 16,
            StorageProfileKind.Nvme => 4,
            _ => 1,
        };
}
'@
Write-Text 'dotnet/RepartoCopier.Core/StorageIoProfile.cs' $storageProfile
git rm dotnet/RepartoCopier.Core/FanoutPerformancePolicy.cs

# 3. CRC32C is the only checksum vocabulary for post-copy verification.
Get-ChildItem dotnet -Recurse -Filter '*.cs' | ForEach-Object {
    $path = $_.FullName
    $content = [System.IO.File]::ReadAllText($path)
    $updated = $content.Replace('FastCrc32', 'FastCrc32C').Replace('VerificationCrc32', 'VerificationCrc32C')
    if ($updated -ne $content) {
        [System.IO.File]::WriteAllText($path, $updated, [System.Text.UTF8Encoding]::new($false))
    }
}
if (Test-Path 'dotnet/RepartoCopier.Core/FastCrc32.cs') { git mv dotnet/RepartoCopier.Core/FastCrc32.cs dotnet/RepartoCopier.Core/FastCrc32C.cs }
if (Test-Path 'dotnet/RepartoCopier.Core.Tests/FastCrc32Tests.cs') { git mv dotnet/RepartoCopier.Core.Tests/FastCrc32Tests.cs dotnet/RepartoCopier.Core.Tests/FastCrc32CTests.cs }

Replace-Exact 'dotnet/RepartoCopier.Core/FastCrc32C.cs' 'internal readonly record struct VerificationBlock(int Length, uint Crc32);' 'internal readonly record struct VerificationBlock(int Length, uint Crc32C);'
Replace-Exact 'dotnet/RepartoCopier.Core/FastVerificationReader.cs' 'current.Expected.Crc32)' 'current.Expected.Crc32C)'

$telemetryPath = 'dotnet/RepartoCopier.Core/CopyTelemetry.cs'
$telemetry = Read-Text $telemetryPath
$telemetry = $telemetry.Replace('VerifyHashBytes', 'VerifyCrc32CBytes')
$telemetry = $telemetry.Replace('VerifyHashTime', 'VerifyCrc32CTime')
$telemetry = $telemetry.Replace('VerifyHashBytesPerSecond', 'VerifyCrc32CBytesPerSecond')
$telemetry = $telemetry.Replace('_verifyHashBytes', '_verifyCrc32CBytes')
$telemetry = $telemetry.Replace('_verifyHashTicks', '_verifyCrc32CTicks')
$telemetry = $telemetry.Replace('RecordVerifyHash', 'RecordVerifyCrc32C')
# Remove compatibility aliases that became self-referential after the canonical rename.
$telemetry = [regex]::Replace($telemetry, '(?m)^\s*public long VerifyCrc32CBytes => VerifyCrc32CBytes;\r?\n', '')
$telemetry = [regex]::Replace($telemetry, '(?m)^\s*public TimeSpan VerifyCrc32CTime => VerifyCrc32CTime;\r?\n', '')
$telemetry = [regex]::Replace($telemetry, '(?m)^\s*public double VerifyCrc32CBytesPerSecond => VerifyCrc32CBytesPerSecond;\r?\n', '')
Write-Text $telemetryPath $telemetry

Get-ChildItem dotnet -Recurse -Filter '*.cs' | ForEach-Object {
    $path = $_.FullName
    $content = [System.IO.File]::ReadAllText($path)
    $updated = $content.Replace('RecordVerifyHash', 'RecordVerifyCrc32C')
    if ($updated -ne $content) { [System.IO.File]::WriteAllText($path, $updated, [System.Text.UTF8Encoding]::new($false)) }
}

# 4. Remove analyzer-proven dead code and unused private parameter.
Replace-Exact 'dotnet/RepartoCopier.Core/CopyEngine.cs' @'
                catch
                {
                    lease?.Dispose();
                    if (budgetOwned)
'@ @'
                catch
                {
                    if (budgetOwned)
'@
Replace-Exact 'dotnet/RepartoCopier.Core/Recovery.cs' @'
        var valid = NormalizeCompletedState(sourceRoot, destinationRoot, files);
'@ @'
        var valid = NormalizeCompletedState(destinationRoot, files);
'@
Replace-Exact 'dotnet/RepartoCopier.Core/Recovery.cs' @'
    private static HashSet<string> NormalizeCompletedState(
        string sourceRoot,
        string destinationRoot,
        IReadOnlyList<RecoveryFile> files)
'@ @'
    private static HashSet<string> NormalizeCompletedState(
        string destinationRoot,
        IReadOnlyList<RecoveryFile> files)
'@

# 5. Public API guards: fail explicitly at the boundary instead of null-dereferencing later.
Replace-Exact 'dotnet/RepartoCopier.Core/CopyEngine.cs' @'
    public static CopyJob Start(CopyPlan plan, CopyOptions? options = null)
    {
        options ??= new CopyOptions(
'@ @'
    public static CopyJob Start(CopyPlan plan, CopyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new CopyOptions(
'@
Replace-Exact 'dotnet/RepartoCopier.Core/CopyEngine.cs' @'
    {
        options ??= new CopyOptions(
            Verify: false,
            SkipSame: plan.SkipSame,
            KeepGoing: plan.KeepGoing);

        var prepared = await Task.Run(() => Preflight(plan), cancellationToken).ConfigureAwait(false);
'@ @'
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new CopyOptions(
            Verify: false,
            SkipSame: plan.SkipSame,
            KeepGoing: plan.KeepGoing);

        var prepared = await Task.Run(() => Preflight(plan), cancellationToken).ConfigureAwait(false);
'@
Replace-Exact 'dotnet/RepartoCopier.Core/CopyPlan.cs' @'
    {
        var added = 0;
        foreach (var raw in selected)
'@ @'
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(selected);
        var added = 0;
        foreach (var raw in selected)
'@
Replace-Exact 'dotnet/RepartoCopier.Core/Settings.cs' '    public static string Render(AppSettings settings) => string.Join(' @'
    public static string Render(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.Join(
'@
# Close the Render block after its final array expression.
$settings = Read-Text 'dotnet/RepartoCopier.Core/Settings.cs'
if ($settings.Contains(']);`r`n`r`n    public static AppSettings Parse')) { throw 'Unexpected literal escapes in Settings.cs' }
$settings = $settings.Replace('        $"show_throughput={settings.ShowThroughput.ToString().ToLowerInvariant()}"`n    ]);`n`n    public static AppSettings Parse', '        $"show_throughput={settings.ShowThroughput.ToString().ToLowerInvariant()}"`n        ]);`n    }`n`n    public static AppSettings Parse')
$settings = $settings.Replace('    public static AppSettings Parse(string text)`n    {`n        var values', '    public static AppSettings Parse(string text)`n    {`n        ArgumentNullException.ThrowIfNull(text);`n        var values')
Write-Text 'dotnet/RepartoCopier.Core/Settings.cs' $settings

# 6. Restrict native kernel32 resolution to the Windows system directory.
foreach ($path in @('dotnet/RepartoCopier.Core/DirectIoSourceReader.cs', 'dotnet/RepartoCopier.Core/DirectIoDestinationWriter.cs')) {
    Replace-Exact $path '        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]' '        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`n        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]'
}

# 7. Architecture contracts: removed layers/names must stay removed.
$contractPath = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'
$contract = Read-Text $contractPath
$contract = $contract.Replace('        var writerMethods = typeof(ExplicitOffsetWriter)`n            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)`n            .Select(method => method.Name)`n            .ToArray();`n        CollectionAssert.DoesNotContain(writerMethods, "WriteTwoAsync");`n`n', '        Assert.IsNull(typeof(CopyEngine).Assembly.GetType("RepartoCopier.Core.ExplicitOffsetWriter"));`n`n')
$needle = '        Assert.IsNull(typeof(CopyEngine).Assembly.GetType("RepartoCopier.Core.StorageWritePolicy"));'
if (-not $contract.Contains($needle)) { throw 'StorageWritePolicy contract anchor missing' }
$contract = $contract.Replace($needle, $needle + "`n        Assert.IsNull(typeof(CopyEngine).Assembly.GetType(\"RepartoCopier.Core.FanoutPerformancePolicy\"));")
$insertAnchor = "    [TestMethod]`n    public void DirectIoAlignmentHasNoSixtyFourKiBCap()"
if (-not $contract.Contains($insertAnchor)) { throw 'Unification insert anchor missing' }
$newTest = @'
    [TestMethod]
    public void VerificationUsesOnlyCrc32CNaming()
    {
        var assembly = typeof(CopyEngine).Assembly;
        Assert.IsNull(assembly.GetType("RepartoCopier.Core.FastCrc32"));
        Assert.IsNotNull(assembly.GetType("RepartoCopier.Core.FastCrc32C"));

        var diagnostics = typeof(CopyDiagnosticsSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.Contains(diagnostics, nameof(CopyDiagnosticsSnapshot.VerifyCrc32CBytes));
        CollectionAssert.Contains(diagnostics, nameof(CopyDiagnosticsSnapshot.VerifyCrc32CTime));
        CollectionAssert.Contains(diagnostics, nameof(CopyDiagnosticsSnapshot.VerifyCrc32CBytesPerSecond));
        CollectionAssert.DoesNotContain(diagnostics, "VerifyHashBytes");
        CollectionAssert.DoesNotContain(diagnostics, "VerifyHashTime");
        CollectionAssert.DoesNotContain(diagnostics, "VerifyHashBytesPerSecond");
    }

'@
$contract = $contract.Replace($insertAnchor, $newTest + $insertAnchor)
Write-Text $contractPath $contract

# 8. Documentation: describe only the productive architecture that exists today.
$readme = Read-Text 'README.md'
$readme = $readme.Replace('verificación post-copia automática mediante CRC32 por bloques', 'verificación post-copia automática mediante CRC32C/Castagnoli por bloques')
$oldPerf = 'El hot path actual usa bloques compartidos de 32 MiB, prefetch acotado, presupuesto global de RAM, backpressure y scheduler por dispositivo físico. En orígenes SSD locales con identidad exacta y alineación conocida existe una ruta Direct I/O `NO_BUFFERING + SEQUENTIAL_SCAN + OVERLAPPED`; la verificación usa el mismo principio cuando es elegible. Las escrituras de destino siguen siendo buffered con offsets explícitos y QD2 selectivo para SSD calificados; Direct I/O de escritura todavía no está implementado.'
$newPerf = 'El hot path usa FAN-OUT con `SharedBlock`, tamaño de transferencia adaptativo, presupuesto dinámico de RAM, replay por rama lenta y `DeviceScheduler` adaptativo por dispositivo físico. Source, destinos y verificación usan Direct I/O `NO_BUFFERING + SEQUENTIAL_SCAN + OVERLAPPED` cuando la topología/alineación lo permiten, con fallback buffered seguro. Cada payload lógico se escribe completo por offset explícito y la concurrencia procede de múltiples bloques/rutas en vuelo, no de fragmentar artificialmente un bloque.'
if (-not $readme.Contains($oldPerf)) { throw 'README performance anchor missing' }
$readme = $readme.Replace($oldPerf, $newPerf)
Write-Text 'README.md' $readme

$testing = Read-Text 'TESTING.md'
$testing = $testing.Replace('FAN-OUT/queue wait', 'FAN-OUT/backpressure')
$testing = $testing.Replace('- `FanoutWaitTime` o `QueueWaitTime` altos: uno o más consumidores o el bus están imponiendo backpressure; no aumentar RAM automáticamente.', '- `FanoutWaitTime` alto o backlog por dispositivo sostenido: uno o más consumidores o el bus están imponiendo backpressure; no aumentar RAM automáticamente.')
$testing = $testing.Replace('- `VerifyHashTime` dominante: el CRC32 es el cuello de CPU y debe optimizarse antes de aumentar I/O; `VerifyReadTime` dominante: el límite es lectura física de destinos.', '- `VerificationBottleneck == Crc32C`: el checksum limita VERIFY; `StorageRead`: limita la lectura física; `Balanced`: ambas tasas de servicio están dentro de la banda del 15%. Usar además `VerifyCrc32CBytesPerSecond` y `VerifyReadBytesPerSecond`.')
Write-Text 'TESTING.md' $testing

$changelog = Read-Text 'CHANGELOG.md'
$unreleasedStart = $changelog.IndexOf('## Unreleased — main')
$v2Start = $changelog.IndexOf('## v2.0.0')
if ($unreleasedStart -lt 0 -or $v2Start -lt 0) { throw 'CHANGELOG anchors missing' }
$unreleased = @'
## Unreleased — main

- FAN-OUT productivo unificado alrededor de `SharedBlock`, replay por rama lenta y tamaño de transferencia adaptativo; eliminadas capas intermedias sin consumidor productivo.
- Direct I/O `NO_BUFFERING + SEQUENTIAL_SCAN + OVERLAPPED` integrado para source, destinos y verify cuando la topología/alineación es elegible, con fallback buffered seguro.
- Escrituras de destino full-block por offset explícito con multi-block in-flight y QD adaptativo por dispositivo; el antiguo QD2 fijo permanece eliminado.
- Verificación post-copia unificada en CRC32C/Castagnoli, incluida nomenclatura de código/telemetría, aceleración hardware, fallback software y clasificación `VerificationBottleneck`.
- BlockSize/bandas fijas eliminadas; `AdaptiveTransferSizer` usa memoria, destinos, QD y feedback de throughput/latencia.
- `StorageWritePolicy`, `FanoutPerformancePolicy` y `ExplicitOffsetWriter` eliminados tras quedar reemplazados por las rutas productivas únicas.
- Documentación y gate CI sincronizados con la arquitectura productiva actual.

'@
$changelog = $changelog.Substring(0, $unreleasedStart) + $unreleased + $changelog.Substring($v2Start)
Write-Text 'CHANGELOG.md' $changelog

$roadmap = Read-Text 'docs/FANOUT-PERFORMANCE-ROADMAP.md'
$roadmap = $roadmap.Replace('StorageWritePolicy y sus tests eliminados tras quedar huérfanos con la migración full-block; el QD productivo queda gobernado únicamente por DeviceScheduler + FanoutPerformancePolicy.', 'StorageWritePolicy, FanoutPerformancePolicy y ExplicitOffsetWriter eliminados tras quedar huérfanos; `StorageIoProfile` clasifica el punto inicial y `DeviceScheduler` gobierna la ruta productiva adaptativa, mientras `DestinationWriteCoordinator` emite la escritura real.')
$roadmap = $roadmap.Replace('La telemetría conserva las métricas históricas `VerifyHash*` por compatibilidad y expone aliases explícitos `VerifyCrc32CBytes`, `VerifyCrc32CTime` y `VerifyCrc32CBytesPerSecond`.', 'La telemetría usa exclusivamente `VerifyCrc32CBytes`, `VerifyCrc32CTime` y `VerifyCrc32CBytesPerSecond`; la nomenclatura histórica `VerifyHash*` fue eliminada para evitar dos contratos para la misma medición.')
Write-Text 'docs/FANOUT-PERFORMANCE-ROADMAP.md' $roadmap

$ci = Read-Text '.github/workflows/windows-dotnet.yml'
$ci = $ci.Replace('# buffered explicit-offset destination writes, recovery, and the WinUI product.', '# Direct I/O destination writes with buffered fallback, multi-block explicit offsets, recovery, and the WinUI product.')
Write-Text '.github/workflows/windows-dotnet.yml' $ci

# 9. Remove temporary audit workflow; final main keeps only permanent CI.
if (Test-Path '.github/workflows/audit-unification.yml') { git rm .github/workflows/audit-unification.yml }

# Normalize whitespace only; do not mass-apply optional style/analyzer suggestions.
dotnet format whitespace RepartoCopier.sln --no-restore --verbosity minimal

git diff --check

Write-Host '=== Post-change forbidden architecture references ==='
$forbidden = @(git grep -n -E 'ExplicitOffsetWriter|FanoutPerformancePolicy|FastCrc32([^C]|$)|VerificationCrc32([^C]|$)|VerifyHash(Bytes|Time|BytesPerSecond)|RecordVerifyHash' -- dotnet docs README.md TESTING.md CHANGELOG.md 2>$null)
if ($forbidden.Count -gt 0) {
    $forbidden | Write-Host
    throw 'Superseded architecture symbols remain.'
}

Write-Host 'Cleanup patch applied successfully.'
