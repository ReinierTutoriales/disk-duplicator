$ErrorActionPreference = 'Stop'

function Read-Text([string]$Path) { [System.IO.File]::ReadAllText((Resolve-Path $Path)) }
function Write-Text([string]$Path, [string]$Content) { [System.IO.File]::WriteAllText((Resolve-Path $Path), $Content, [System.Text.UTF8Encoding]::new($false)) }
function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $content = Read-Text $Path
    if (-not $content.Contains($Old)) { throw "Expected text not found in $Path`n$Old" }
    Write-Text $Path ($content.Replace($Old, $New))
}

Write-Host '=== Baseline legacy references ==='
git grep -n -E 'ExplicitOffsetWriter|FanoutPerformancePolicy|FastCrc32|VerificationCrc32|VerifyHash|RecordVerifyHash' -- dotnet docs README.md TESTING.md CHANGELOG.md 2>$null | Write-Host

# Single productive destination write primitive.
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

# Single storage profile policy type.
$profile = @'
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

    private static StorageProfileKind Classify(StorageDeviceInfo device, DeviceIdentityConfidence confidence)
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

    private static int InitialQueueDepthFor(StorageProfileKind kind, DeviceIdentityConfidence confidence) => kind switch
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
Write-Text 'dotnet/RepartoCopier.Core/StorageIoProfile.cs' $profile
git rm dotnet/RepartoCopier.Core/FanoutPerformancePolicy.cs

# CRC32C is the sole verification checksum vocabulary.
Get-ChildItem dotnet -Recurse -Filter '*.cs' | ForEach-Object {
    $text = [System.IO.File]::ReadAllText($_.FullName)
    $next = $text.Replace('FastCrc32', 'FastCrc32C').Replace('VerificationCrc32', 'VerificationCrc32C')
    if ($next -ne $text) { [System.IO.File]::WriteAllText($_.FullName, $next, [System.Text.UTF8Encoding]::new($false)) }
}
git mv dotnet/RepartoCopier.Core/FastCrc32.cs dotnet/RepartoCopier.Core/FastCrc32C.cs
git mv dotnet/RepartoCopier.Core.Tests/FastCrc32Tests.cs dotnet/RepartoCopier.Core.Tests/FastCrc32CTests.cs
Replace-Exact 'dotnet/RepartoCopier.Core/FastCrc32C.cs' 'internal readonly record struct VerificationBlock(int Length, uint Crc32);' 'internal readonly record struct VerificationBlock(int Length, uint Crc32C);'
$verify = Read-Text 'dotnet/RepartoCopier.Core/FastVerificationReader.cs'
$verify = $verify.Replace('current.Expected.Crc32)', 'current.Expected.Crc32C)')
$verify = $verify.Replace('RecordVerifyHash', 'RecordVerifyCrc32C')
Write-Text 'dotnet/RepartoCopier.Core/FastVerificationReader.cs' $verify

$telemetryPath = 'dotnet/RepartoCopier.Core/CopyTelemetry.cs'
$t = Read-Text $telemetryPath
$t = $t.Replace('VerifyHashBytesPerSecond', 'VerifyCrc32CBytesPerSecond')
$t = $t.Replace('VerifyHashBytes', 'VerifyCrc32CBytes')
$t = $t.Replace('VerifyHashTime', 'VerifyCrc32CTime')
$t = $t.Replace('_verifyHashBytes', '_verifyCrc32CBytes')
$t = $t.Replace('_verifyHashTicks', '_verifyCrc32CTicks')
$t = $t.Replace('RecordVerifyHash', 'RecordVerifyCrc32C')
$t = [regex]::Replace($t, '(?m)^\s*public long VerifyCrc32CBytes => VerifyCrc32CBytes;\r?\n', '')
$t = [regex]::Replace($t, '(?m)^\s*public TimeSpan VerifyCrc32CTime => VerifyCrc32CTime;\r?\n', '')
$t = [regex]::Replace($t, '(?m)^\s*public double VerifyCrc32CBytesPerSecond => VerifyCrc32CBytesPerSecond;\r?\n', '')
Write-Text $telemetryPath $t

# Analyzer-proven dead code and unused private parameter.
$enginePath = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$engine = Read-Text $enginePath
$dead = @'
                catch
                {
                    lease?.Dispose();
                    if (budgetOwned)
'@
$live = @'
                catch
                {
                    if (budgetOwned)
'@
if (-not $engine.Contains($dead)) { throw 'Dead lease cleanup anchor missing' }
$engine = $engine.Replace($dead, $live)
# Null boundary guards for public copy entry points.
$engine = [regex]::Replace($engine, '(public static CopyJob Start\(CopyPlan plan, CopyOptions\? options = null\)\s*\{\s*)', '$1        ArgumentNullException.ThrowIfNull(plan);' + [Environment]::NewLine, 1)
$engine = [regex]::Replace($engine, '(public static async Task<CopyJob> StartAsync\([\s\S]*?CancellationToken cancellationToken = default\)\s*\{\s*)', '$1        ArgumentNullException.ThrowIfNull(plan);' + [Environment]::NewLine, 1)
Write-Text $enginePath $engine

$recoveryPath = 'dotnet/RepartoCopier.Core/Recovery.cs'
$r = Read-Text $recoveryPath
$r = $r.Replace('NormalizeCompletedState(sourceRoot, destinationRoot, files)', 'NormalizeCompletedState(destinationRoot, files)')
$r = $r.Replace("    private static HashSet<string> NormalizeCompletedState(`r`n        string sourceRoot,`r`n        string destinationRoot,", "    private static HashSet<string> NormalizeCompletedState(`r`n        string destinationRoot,")
$r = $r.Replace("    private static HashSet<string> NormalizeCompletedState(`n        string sourceRoot,`n        string destinationRoot,", "    private static HashSet<string> NormalizeCompletedState(`n        string destinationRoot,")
if ($r.Contains('NormalizeCompletedState(sourceRoot')) { throw 'Unused recovery sourceRoot remains' }
Write-Text $recoveryPath $r

$planPath = 'dotnet/RepartoCopier.Core/CopyPlan.cs'
$p = Read-Text $planPath
$anchor = @'
    {
        var added = 0;
        foreach (var raw in selected)
'@
$replacement = @'
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(selected);
        var added = 0;
        foreach (var raw in selected)
'@
if (-not $p.Contains($anchor)) { throw 'CopyPlan guard anchor missing' }
Write-Text $planPath ($p.Replace($anchor, $replacement))

# Restrict kernel32 P/Invoke resolution to System32.
$attr = "        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`r`n        [DllImport(\"kernel32.dll\", EntryPoint = \"CreateFileW\", SetLastError = true, CharSet = CharSet.Unicode)]"
foreach ($path in @('dotnet/RepartoCopier.Core/DirectIoSourceReader.cs', 'dotnet/RepartoCopier.Core/DirectIoDestinationWriter.cs')) {
    $x = Read-Text $path
    $old = '        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]'
    if (-not $x.Contains($old)) { throw "PInvoke anchor missing: $path" }
    $newline = if ($x.Contains("`r`n")) { "`r`n" } else { "`n" }
    $new = '        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]' + $newline + $old
    Write-Text $path ($x.Replace($old, $new))
}

# Architecture contracts lock removed layers and old checksum names out.
$contractPath = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'
$c = Read-Text $contractPath
$oldWriterContract = @'
        var writerMethods = typeof(ExplicitOffsetWriter)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(writerMethods, "WriteTwoAsync");

'@
if (-not $c.Contains($oldWriterContract)) { throw 'ExplicitOffsetWriter contract anchor missing' }
$c = $c.Replace($oldWriterContract, '        Assert.IsNull(typeof(CopyEngine).Assembly.GetType("RepartoCopier.Core.ExplicitOffsetWriter"));' + [Environment]::NewLine + [Environment]::NewLine)
$storageAnchor = '        Assert.IsNull(typeof(CopyEngine).Assembly.GetType("RepartoCopier.Core.StorageWritePolicy"));'
if (-not $c.Contains($storageAnchor)) { throw 'Storage policy contract anchor missing' }
$c = $c.Replace($storageAnchor, $storageAnchor + [Environment]::NewLine + '        Assert.IsNull(typeof(CopyEngine).Assembly.GetType("RepartoCopier.Core.FanoutPerformancePolicy"));')
$methodAnchor = '    public void DirectIoAlignmentHasNoSixtyFourKiBCap()'
$pos = $c.IndexOf($methodAnchor)
if ($pos -lt 0) { throw 'Contract insertion anchor missing' }
$attributePos = $c.LastIndexOf('    [TestMethod]', $pos)
$newContract = @'
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
$c = $c.Insert($attributePos, $newContract)
Write-Text $contractPath $c

# Documentation must describe current code, never superseded architecture.
$readme = Read-Text 'README.md'
$readme = $readme.Replace('verificación post-copia automática mediante CRC32 por bloques', 'verificación post-copia automática mediante CRC32C/Castagnoli por bloques')
$oldPerf = 'El hot path actual usa bloques compartidos de 32 MiB, prefetch acotado, presupuesto global de RAM, backpressure y scheduler por dispositivo físico. En orígenes SSD locales con identidad exacta y alineación conocida existe una ruta Direct I/O `NO_BUFFERING + SEQUENTIAL_SCAN + OVERLAPPED`; la verificación usa el mismo principio cuando es elegible. Las escrituras de destino siguen siendo buffered con offsets explícitos y QD2 selectivo para SSD calificados; Direct I/O de escritura todavía no está implementado.'
$newPerf = 'El hot path usa FAN-OUT con `SharedBlock`, tamaño de transferencia adaptativo, presupuesto dinámico de RAM, replay por rama lenta y `DeviceScheduler` adaptativo por dispositivo físico. Source, destinos y verificación usan Direct I/O `NO_BUFFERING + SEQUENTIAL_SCAN + OVERLAPPED` cuando la topología/alineación lo permiten, con fallback buffered seguro. Cada payload lógico se escribe completo por offset explícito y la concurrencia procede de múltiples bloques/rutas en vuelo, no de fragmentar artificialmente un bloque.'
if (-not $readme.Contains($oldPerf)) { throw 'README architecture anchor missing' }
Write-Text 'README.md' ($readme.Replace($oldPerf, $newPerf))

$testing = Read-Text 'TESTING.md'
$testing = $testing.Replace('FAN-OUT/queue wait', 'FAN-OUT/backpressure')
$testing = $testing.Replace('- `FanoutWaitTime` o `QueueWaitTime` altos: uno o más consumidores o el bus están imponiendo backpressure; no aumentar RAM automáticamente.', '- `FanoutWaitTime` alto o backlog por dispositivo sostenido: uno o más consumidores o el bus están imponiendo backpressure; no aumentar RAM automáticamente.')
$testing = $testing.Replace('- `VerifyHashTime` dominante: el CRC32 es el cuello de CPU y debe optimizarse antes de aumentar I/O; `VerifyReadTime` dominante: el límite es lectura física de destinos.', '- `VerificationBottleneck == Crc32C`: el checksum limita VERIFY; `StorageRead`: limita la lectura física; `Balanced`: ambas tasas de servicio están dentro de la banda del 15%. Usar además `VerifyCrc32CBytesPerSecond` y `VerifyReadBytesPerSecond`.')
Write-Text 'TESTING.md' $testing

$changelog = Read-Text 'CHANGELOG.md'
$u = $changelog.IndexOf('## Unreleased — main')
$v = $changelog.IndexOf('## v2.0.0')
if ($u -lt 0 -or $v -lt 0) { throw 'CHANGELOG anchors missing' }
$newUnreleased = @'
## Unreleased — main

- FAN-OUT productivo unificado alrededor de `SharedBlock`, replay por rama lenta y tamaño de transferencia adaptativo; eliminadas capas intermedias sin consumidor productivo.
- Direct I/O `NO_BUFFERING + SEQUENTIAL_SCAN + OVERLAPPED` integrado para source, destinos y verify cuando la topología/alineación es elegible, con fallback buffered seguro.
- Escrituras full-block por offset explícito con multi-block in-flight y QD adaptativo por dispositivo; el antiguo QD2 fijo permanece eliminado.
- Verificación unificada en CRC32C/Castagnoli, incluida nomenclatura de código/telemetría, aceleración hardware, fallback software y clasificación `VerificationBottleneck`.
- `StorageWritePolicy`, `FanoutPerformancePolicy` y `ExplicitOffsetWriter` eliminados tras quedar reemplazados por las rutas productivas únicas.
- Documentación y CI sincronizados con la arquitectura productiva actual.

'@
Write-Text 'CHANGELOG.md' ($changelog.Substring(0, $u) + $newUnreleased + $changelog.Substring($v))

$roadmapPath = 'docs/FANOUT-PERFORMANCE-ROADMAP.md'
$roadmap = Read-Text $roadmapPath
$roadmap = $roadmap.Replace('StorageWritePolicy y sus tests eliminados tras quedar huérfanos con la migración full-block; el QD productivo queda gobernado únicamente por DeviceScheduler + FanoutPerformancePolicy.', 'StorageWritePolicy, FanoutPerformancePolicy y ExplicitOffsetWriter eliminados tras quedar huérfanos; `StorageIoProfile` clasifica el punto inicial y `DeviceScheduler` gobierna la ruta productiva adaptativa, mientras `DestinationWriteCoordinator` emite la escritura real.')
$roadmap = $roadmap.Replace('La telemetría conserva las métricas históricas `VerifyHash*` por compatibilidad y expone aliases explícitos `VerifyCrc32CBytes`, `VerifyCrc32CTime` y `VerifyCrc32CBytesPerSecond`.', 'La telemetría usa exclusivamente `VerifyCrc32CBytes`, `VerifyCrc32CTime` y `VerifyCrc32CBytesPerSecond`; la nomenclatura histórica `VerifyHash*` fue eliminada para evitar dos contratos para la misma medición.')
Write-Text $roadmapPath $roadmap

$ciPath = '.github/workflows/windows-dotnet.yml'
$ci = Read-Text $ciPath
$ci = $ci.Replace('# buffered explicit-offset destination writes, recovery, and the WinUI product.', '# Direct I/O destination writes with buffered fallback, multi-block explicit offsets, recovery, and the WinUI product.')
Write-Text $ciPath $ci

# Remove temporary audit infrastructure from final main.
if (Test-Path '.github/workflows/audit-unification.yml') { git rm .github/workflows/audit-unification.yml }

# Whitespace only: optional style changes are deliberately not mass-applied.
dotnet format whitespace RepartoCopier.sln --no-restore --verbosity minimal
git diff --check

Write-Host '=== Forbidden legacy references after cleanup ==='
$forbidden = @(git grep -n -E 'ExplicitOffsetWriter|FanoutPerformancePolicy|FastCrc32([^C]|$)|VerificationCrc32([^C]|$)|VerifyHash(Bytes|Time|BytesPerSecond)|RecordVerifyHash' -- dotnet docs README.md TESTING.md CHANGELOG.md 2>$null)
if ($forbidden.Count -gt 0) {
    $forbidden | Write-Host
    throw 'Superseded architecture symbols remain.'
}

Write-Host 'Unification cleanup applied.'
