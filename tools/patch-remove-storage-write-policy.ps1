$ErrorActionPreference = 'Stop'

$policyPath = 'dotnet/RepartoCopier.Core/StorageWritePolicy.cs'
$policyTestsPath = 'dotnet/RepartoCopier.Core.Tests/StorageWritePolicyTests.cs'
$performanceTestsPath = 'dotnet/RepartoCopier.Core.Tests/PerformanceFreedomArchitectureTests.cs'
$contractPath = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'
$roadmapPath = 'docs/FANOUT-PERFORMANCE-ROADMAP.md'

if (Test-Path $policyPath) { Remove-Item -LiteralPath $policyPath -Force }
if (Test-Path $policyTestsPath) { Remove-Item -LiteralPath $policyTestsPath -Force }

$performanceTests = @'
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class PerformanceFreedomArchitectureTests
{
    [TestMethod]
    public void FixedSmallFilePerformanceFloorsStayRemoved()
    {
        Assert.IsNull(typeof(CopyEngine).Assembly.GetType("RepartoCopier.Core.StorageWritePolicy"));

        var directWriterFields = typeof(DirectIoDestinationWriter)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => field.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(directWriterFields, "MinimumFileSize");

        var copyEngineFields = typeof(CopyEngine)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => field.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(copyEngineFields, "SourcePrefetchThreshold");
    }

    [TestMethod]
    public void DirectWriteEligibilityHasNoArtificialSizeFloor()
    {
        var device = ExactLocalNvme();

        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(device, 1));
        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(device, 4096));
        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(device, 1024 * 1024));
    }

    [TestMethod]
    public void AlignedFanoutPayloadCanServeDirectDestinationsWithoutRestaging()
    {
        using var payload = SourceBufferLease.RentAligned(1024 * 1024, DirectIoSourceReader.MaximumSupportedAlignment);

        Assert.IsTrue(payload.IsAlignedFor(512));
        Assert.IsTrue(payload.IsAlignedFor(4096));
        Assert.IsTrue(payload.IsAlignedFor(64 * 1024));
    }

    private static StorageDeviceInfo ExactLocalNvme() =>
        new(
            @"E:\copy",
            @"E:\",
            4,
            1,
            "NVMe",
            StorageMediaKind.SolidState,
            false,
            512,
            4096,
            true,
            null,
            false,
            "NTFS",
            DriveType.Fixed,
            false,
            true,
            true,
            0);
}
'@
Set-Content -LiteralPath $performanceTestsPath -Value $performanceTests -Encoding utf8

$contract = Get-Content -LiteralPath $contractPath -Raw
$oldContract = @'
        var policyMethods = typeof(StorageWritePolicy)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(policyMethods, "BufferedLargeWriteQueueDepth");
        CollectionAssert.Contains(policyMethods, "LargeWriteQueueDepth");

'@
$newContract = @'
        Assert.IsNull(typeof(CopyEngine).Assembly.GetType("RepartoCopier.Core.StorageWritePolicy"));

'@
if (-not $contract.Contains($oldContract)) { throw 'StorageWritePolicy contract block not found.' }
$contract = $contract.Replace($oldContract, $newContract)
Set-Content -LiteralPath $contractPath -Value $contract -Encoding utf8

$roadmap = Get-Content -LiteralPath $roadmapPath -Raw
$oldCoordinator = '- `DestinationWriteCoordinator` divide cada payload en operaciones de offset explícito y cada sub-I/O adquiere un lease del `DeviceScheduler` físico.'
$newCoordinator = '- `DestinationWriteCoordinator` emite cada payload FAN-OUT lógico como una sola escritura de offset explícito; la concurrencia física ocurre entre bloques independientes y ramas de destino, no fragmentando un bloque secuencial para fabricar QD.'
if (-not $roadmap.Contains($oldCoordinator)) { throw 'Coordinator roadmap line not found.' }
$roadmap = $roadmap.Replace($oldCoordinator, $newCoordinator)

$oldRamp = @'
### P1 — ramp-up de QD más rápido y basado en evidencia

`DeviceScheduler.RecordCompletionLocked` todavía espera una cantidad de completions dependiente del QD antes de reevaluar. No existe hard max, pero una copia corta puede terminar antes de explorar suficiente profundidad. Auditar tiempo-hasta-QD-óptimo y sustituir cualquier lentitud innecesaria por exploración basada en demanda, throughput, latencia y tiempo observado; no por otro número fijo arbitrario.

'@
$newRamp = @'
### VALIDACIÓN FÍSICA — ramp-up de QD

`DeviceScheduler.RecordCompletionLocked` ya reevalúa usando una onda de concurrencia realmente observada (`_samplePeakObservedConcurrency`) y demanda efectiva, sin un piso fijo arbitrario de completions. El siguiente paso no es volver a cambiar la política por intuición: medir tiempo-hasta-QD-útil en hardware físico y modificarla solo si el benchmark demuestra una exploración insuficiente.

'@
if (-not $roadmap.Contains($oldRamp)) { throw 'QD ramp-up roadmap block not found.' }
$roadmap = $roadmap.Replace($oldRamp, $newRamp)

$oldNetwork = @'
### P1 — eliminar QD1 incondicional de network

`StorageWritePolicy` todavía fuerza network a QD1. Direct I/O remoto puede seguir deshabilitado por compatibilidad, pero el buffered explicit-offset path no debe asumir que NAS/SMB solo soporta una operación concurrente. Convertirlo en exploración adaptativa.

'@
if (-not $roadmap.Contains($oldNetwork)) { throw 'Network QD1 roadmap block not found.' }
$roadmap = $roadmap.Replace($oldNetwork, '')

$closedAnchor = '- Descriptor `PendingRead` de verificación convertido a valor readonly para eliminar la asignación de heap por entrada pendiente.'
$closedReplacement = $closedAnchor + "`r`n- `StorageWritePolicy` y sus tests eliminados tras quedar huérfanos con la migración full-block; el QD productivo queda gobernado únicamente por `DeviceScheduler` + `FanoutPerformancePolicy`."
if (-not $roadmap.Contains($closedAnchor)) { throw 'Closed-items anchor not found.' }
$roadmap = $roadmap.Replace($closedAnchor, $closedReplacement)

Set-Content -LiteralPath $roadmapPath -Value $roadmap -Encoding utf8

git diff --check
git status --short
