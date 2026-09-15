$ErrorActionPreference = 'Stop'

$telemetryPath = 'dotnet/RepartoCopier.Core/CopyTelemetry.cs'
$enginePath = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$contractPath = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'
$testsPath = 'dotnet/RepartoCopier.Core.Tests/VerificationDiagnosticsTests.cs'
$roadmapPath = 'docs/FANOUT-PERFORMANCE-ROADMAP.md'

$telemetry = Get-Content -LiteralPath $telemetryPath -Raw
$enumAnchor = 'public sealed record PipelineGovernorSnapshot('
$enumText = @'
public enum VerificationBottleneckKind
{
    None,
    StorageRead,
    Crc32C,
    Balanced,
}

'@
if (-not $telemetry.Contains($enumAnchor)) { throw 'Telemetry enum anchor not found.' }
$telemetry = $telemetry.Replace($enumAnchor, $enumText + $enumAnchor)

$rateAnchor = @'
    public double VerifyReadBytesPerSecond => Rate(VerifyReadBytes, VerifyReadTime);
    public double VerifyHashBytesPerSecond => Rate(VerifyHashBytes, VerifyHashTime);
'@
$rateReplacement = @'
    public double VerifyReadBytesPerSecond => Rate(VerifyReadBytes, VerifyReadTime);
    public double VerifyHashBytesPerSecond => Rate(VerifyHashBytes, VerifyHashTime);
    public long VerifyCrc32CBytes => VerifyHashBytes;
    public TimeSpan VerifyCrc32CTime => VerifyHashTime;
    public double VerifyCrc32CBytesPerSecond => Rate(VerifyCrc32CBytes, VerifyCrc32CTime);
    public VerificationBottleneckKind VerificationBottleneck => ClassifyVerificationBottleneck(
        VerifyReadBytesPerSecond,
        VerifyCrc32CBytesPerSecond,
        VerifyReadBytes,
        VerifyCrc32CBytes);
'@
if (-not $telemetry.Contains($rateAnchor)) { throw 'Telemetry rate anchor not found.' }
$telemetry = $telemetry.Replace($rateAnchor, $rateReplacement)

$classifyAnchor = @'
    private static double Rate(long bytes, TimeSpan elapsed) =>
        bytes <= 0 || elapsed <= TimeSpan.Zero ? 0 : bytes / elapsed.TotalSeconds;
'@
$classifyReplacement = @'
    private static double Rate(long bytes, TimeSpan elapsed) =>
        bytes <= 0 || elapsed <= TimeSpan.Zero ? 0 : bytes / elapsed.TotalSeconds;

    private static VerificationBottleneckKind ClassifyVerificationBottleneck(
        double readBytesPerSecond,
        double crc32CBytesPerSecond,
        long readBytes,
        long crc32CBytes)
    {
        if (readBytes <= 0 || crc32CBytes <= 0 || readBytesPerSecond <= 0 || crc32CBytesPerSecond <= 0)
            return VerificationBottleneckKind.None;

        const double materialDifference = 0.85;
        if (readBytesPerSecond < crc32CBytesPerSecond * materialDifference)
            return VerificationBottleneckKind.StorageRead;
        if (crc32CBytesPerSecond < readBytesPerSecond * materialDifference)
            return VerificationBottleneckKind.Crc32C;
        return VerificationBottleneckKind.Balanced;
    }
'@
if (-not $telemetry.Contains($classifyAnchor)) { throw 'Telemetry classify anchor not found.' }
$telemetry = $telemetry.Replace($classifyAnchor, $classifyReplacement)
Set-Content -LiteralPath $telemetryPath -Value $telemetry -Encoding utf8

$engine = Get-Content -LiteralPath $enginePath -Raw
$oldMessage = 'CRC32 no coincide durante verificación:'
$newMessage = 'CRC32C no coincide durante verificación:'
if (-not $engine.Contains($oldMessage)) { throw 'CRC32 verification message anchor not found.' }
$engine = $engine.Replace($oldMessage, $newMessage)
Set-Content -LiteralPath $enginePath -Value $engine -Encoding utf8

@'
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class VerificationDiagnosticsTests
{
    private const int OneMiB = 1024 * 1024;

    [TestMethod]
    public void VerificationClassifiesStorageReadWhenReadServiceIsSlower()
    {
        var telemetry = new CopyTelemetry();
        telemetry.RecordVerifyRead(OneMiB, TimeSpan.FromMilliseconds(400));
        telemetry.RecordVerifyHash(OneMiB, TimeSpan.FromMilliseconds(100));

        var snapshot = telemetry.Snapshot();

        Assert.AreEqual(VerificationBottleneckKind.StorageRead, snapshot.VerificationBottleneck);
        Assert.AreEqual(snapshot.VerifyHashBytes, snapshot.VerifyCrc32CBytes);
        Assert.AreEqual(snapshot.VerifyHashTime, snapshot.VerifyCrc32CTime);
        Assert.AreEqual(snapshot.VerifyHashBytesPerSecond, snapshot.VerifyCrc32CBytesPerSecond);
    }

    [TestMethod]
    public void VerificationClassifiesCrc32CWhenChecksumServiceIsSlower()
    {
        var telemetry = new CopyTelemetry();
        telemetry.RecordVerifyRead(OneMiB, TimeSpan.FromMilliseconds(100));
        telemetry.RecordVerifyHash(OneMiB, TimeSpan.FromMilliseconds(400));

        Assert.AreEqual(VerificationBottleneckKind.Crc32C, telemetry.Snapshot().VerificationBottleneck);
    }

    [TestMethod]
    public void VerificationClassifiesBalancedInsideMaterialDifferenceBand()
    {
        var telemetry = new CopyTelemetry();
        telemetry.RecordVerifyRead(OneMiB, TimeSpan.FromMilliseconds(100));
        telemetry.RecordVerifyHash(OneMiB, TimeSpan.FromMilliseconds(110));

        Assert.AreEqual(VerificationBottleneckKind.Balanced, telemetry.Snapshot().VerificationBottleneck);
    }

    [TestMethod]
    public void VerificationHasNoBottleneckClassificationWithoutBothSamples()
    {
        var telemetry = new CopyTelemetry();
        telemetry.RecordVerifyRead(OneMiB, TimeSpan.FromMilliseconds(100));

        Assert.AreEqual(VerificationBottleneckKind.None, telemetry.Snapshot().VerificationBottleneck);
    }
}
'@ | Set-Content -LiteralPath $testsPath -Encoding utf8

$contract = Get-Content -LiteralPath $contractPath -Raw
$contractAnchor = @'
    [TestMethod]
    public void DirectIoAlignmentHasNoSixtyFourKiBCap()
'@
$contractInsert = @'
    [TestMethod]
    public void VerificationDiagnosticsExposeReadVsCrc32CBottleneck()
    {
        var properties = typeof(CopyDiagnosticsSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.Contains(properties, nameof(CopyDiagnosticsSnapshot.VerifyCrc32CBytes));
        CollectionAssert.Contains(properties, nameof(CopyDiagnosticsSnapshot.VerifyCrc32CTime));
        CollectionAssert.Contains(properties, nameof(CopyDiagnosticsSnapshot.VerifyCrc32CBytesPerSecond));
        CollectionAssert.Contains(properties, nameof(CopyDiagnosticsSnapshot.VerificationBottleneck));
        Assert.AreEqual(typeof(VerificationBottleneckKind),
            typeof(CopyDiagnosticsSnapshot).GetProperty(nameof(CopyDiagnosticsSnapshot.VerificationBottleneck))!.PropertyType);
    }

'@
if (-not $contract.Contains($contractAnchor)) { throw 'Unification contract insertion anchor not found.' }
$contract = $contract.Replace($contractAnchor, $contractInsert + $contractAnchor)
Set-Content -LiteralPath $contractPath -Value $contract -Encoding utf8

$roadmap = Get-Content -LiteralPath $roadmapPath -Raw
$oldRoadmap = @'
### P2 — CPU por byte / verificación

CRC32C interno ya dispone de ruta hardware y fallback software Castagnoli bit-idéntico. El siguiente paso no es cambiar otra vez de algoritmo: medir GiB/s de lectura, tiempo de checksum y CPU en hardware real para comprobar cuánto aporta la aceleración y si la verificación está limitada por I/O o CPU.
'@
$newRoadmap = @'
### VALIDACIÓN FÍSICA — CPU por byte / verificación

CRC32C interno ya dispone de ruta hardware y fallback software Castagnoli bit-idéntico. La telemetría conserva las métricas históricas `VerifyHash*` por compatibilidad y expone aliases explícitos `VerifyCrc32CBytes`, `VerifyCrc32CTime` y `VerifyCrc32CBytesPerSecond`. `VerificationBottleneck` clasifica la tasa de servicio como `StorageRead`, `Crc32C`, `Balanced` (banda del 15%) o `None` sin muestras suficientes. El mensaje de mismatch productivo también identifica CRC32C. Pendiente únicamente validar en hardware real GiB/s, CPU y la clasificación frente al tiempo de pared.
'@
if (-not $roadmap.Contains($oldRoadmap)) { throw 'Verification roadmap block not found.' }
$roadmap = $roadmap.Replace($oldRoadmap, $newRoadmap)
$roadmap = $roadmap.Replace('- fan-out wait, buffer wait y hash time;', '- fan-out wait, buffer wait, verify read GiB/s, CRC32C GiB/s y `VerificationBottleneck`;')
Set-Content -LiteralPath $roadmapPath -Value $roadmap -Encoding utf8

git status --short
