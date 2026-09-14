$ErrorActionPreference = 'Stop'

$coordinatorPath = 'dotnet/RepartoCopier.Core/DestinationWriteCoordinator.cs'
$directPath = 'dotnet/RepartoCopier.Core/DirectIoDestinationWriter.cs'
$copyPath = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$fastTestsPath = 'dotnet/RepartoCopier.Core.Tests/ProductionFastPathTests.cs'
$contractPath = 'dotnet/RepartoCopier.Core.Tests/SequentialBlockWriteArchitectureTests.cs'

@'
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Issues one logical FAN-OUT payload as one explicit-offset write.
/// Concurrency belongs between independent blocks and destination branches;
/// a large sequential block is never fragmented merely to manufacture queue depth.
/// </summary>
internal static class DestinationWriteCoordinator
{
    internal static async Task<int> WriteAsync(
        SafeFileHandle handle,
        ReadOnlyMemory<byte> data,
        long baseOffset,
        DeviceScheduler scheduler,
        CancellationToken token,
        int requiredAlignment = 1)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        if (requiredAlignment <= 0 || (requiredAlignment & (requiredAlignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(requiredAlignment));
        if (requiredAlignment > 1 && (baseOffset % requiredAlignment != 0 || data.Length % requiredAlignment != 0))
            throw new ArgumentException("La escritura Direct I/O debe comenzar y terminar en límites de sector.");
        if (data.IsEmpty)
            return 0;

        using var io = await scheduler.AcquireIoAsync(data.Length, token).ConfigureAwait(false);
        await ExplicitOffsetWriter.WriteOneAsync(handle, data, baseOffset, token).ConfigureAwait(false);
        return 1;
    }
}
'@ | Set-Content -LiteralPath $coordinatorPath -Encoding utf8

$direct = Get-Content -LiteralPath $directPath -Raw
$oldSignature = @'
            bool payloadIsAligned,
            int requestedDepth,
            int minimumSliceBytes,
            DeviceScheduler scheduler,
'@
$newSignature = @'
            bool payloadIsAligned,
            DeviceScheduler scheduler,
'@
if (-not $direct.Contains($oldSignature)) { throw 'DirectIoDestinationWriter signature pattern not found.' }
$direct = $direct.Replace($oldSignature, $newSignature)

$oldAligned = @'
                        fileOffset,
                        requestedDepth,
                        minimumSliceBytes,
                        scheduler,
                        token,
                        Alignment).ConfigureAwait(false);
'@
$newAligned = @'
                        fileOffset,
                        scheduler,
                        token,
                        Alignment).ConfigureAwait(false);
'@
if (-not $direct.Contains($oldAligned)) { throw 'Direct aligned coordinator call pattern not found.' }
$direct = $direct.Replace($oldAligned, $newAligned)

$oldTail = @'
                        checked(fileOffset + alignedLength),
                        1,
                        minimumSliceBytes,
                        scheduler,
                        token,
                        Alignment).ConfigureAwait(false);
'@
$newTail = @'
                        checked(fileOffset + alignedLength),
                        scheduler,
                        token,
                        Alignment).ConfigureAwait(false);
'@
if (-not $direct.Contains($oldTail)) { throw 'Direct tail coordinator call pattern not found.' }
$direct = $direct.Replace($oldTail, $newTail)
Set-Content -LiteralPath $directPath -Value $direct -Encoding utf8

$copy = Get-Content -LiteralPath $copyPath -Raw
$oldDirect = @'
                var queueDepth = StorageWritePolicy.LargeWriteQueueDepth(
                    worker.Device,
                    worker.DeviceScheduler.ExplorationQueueDepth,
                    data.Length);
                var started = Stopwatch.GetTimestamp();
                int operations;
                try
                {
                    operations = await direct.WriteAsync(
                        data,
                        offset,
                        current.Entry.Size,
                        payloadIsAligned: true,
                        queueDepth,
                        StorageWritePolicy.MinimumParallelSliceBytes,
                        worker.DeviceScheduler,
                        job.Token).ConfigureAwait(false);
'@
$newDirect = @'
                var started = Stopwatch.GetTimestamp();
                int operations;
                try
                {
                    operations = await direct.WriteAsync(
                        data,
                        offset,
                        current.Entry.Size,
                        payloadIsAligned: true,
                        worker.DeviceScheduler,
                        job.Token).ConfigureAwait(false);
'@
if (-not $copy.Contains($oldDirect)) { throw 'CopyEngine direct write pattern not found.' }
$copy = $copy.Replace($oldDirect, $newDirect)

$oldBuffered = @'
                var queueDepth = StorageWritePolicy.LargeWriteQueueDepth(
                    worker.Device,
                    worker.DeviceScheduler.ExplorationQueueDepth,
                    data.Length);
                var started = Stopwatch.GetTimestamp();
                var operations = await DestinationWriteCoordinator.WriteAsync(
                    stream.SafeFileHandle,
                    data,
                    offset,
                    queueDepth,
                    StorageWritePolicy.MinimumParallelSliceBytes,
                    worker.DeviceScheduler,
                    job.Token).ConfigureAwait(false);
'@
$newBuffered = @'
                var started = Stopwatch.GetTimestamp();
                var operations = await DestinationWriteCoordinator.WriteAsync(
                    stream.SafeFileHandle,
                    data,
                    offset,
                    worker.DeviceScheduler,
                    job.Token).ConfigureAwait(false);
'@
if (-not $copy.Contains($oldBuffered)) { throw 'CopyEngine buffered write pattern not found.' }
$copy = $copy.Replace($oldBuffered, $newBuffered)
Set-Content -LiteralPath $copyPath -Value $copy -Encoding utf8

$tests = Get-Content -LiteralPath $fastTestsPath -Raw
$tests = $tests.Replace(
    'public async Task LargeSharedBlocksPreserveLogicalFanOutWhileAllowingVariablePhysicalWriteDepth()',
    'public async Task LargeSharedBlocksRemainSinglePhysicalWritesPerDestination()')
$oldAssert = '        Assert.IsGreaterThanOrEqualTo((long)destinations.Length, metrics.WriteOperations);'
$newAssert = '        Assert.AreEqual((long)destinations.Length, metrics.WriteOperations, "Un bloque FAN-OUT menor de 32 MiB debe permanecer como una sola escritura física por destino.");'
if (-not $tests.Contains($oldAssert)) { throw 'ProductionFastPath write-operation assertion not found.' }
$tests = $tests.Replace($oldAssert, $newAssert)
Set-Content -LiteralPath $fastTestsPath -Value $tests -Encoding utf8

@'
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SequentialBlockWriteArchitectureTests
{
    [TestMethod]
    public void DestinationCoordinatorDoesNotExposeIntraBlockDepthOrSliceControls()
    {
        var type = typeof(CopyEngine).Assembly.GetType("RepartoCopier.Core.DestinationWriteCoordinator", throwOnError: true)!;
        var write = type.GetMethod("WriteAsync", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("DestinationWriteCoordinator.WriteAsync no existe.");
        var names = write.GetParameters().Select(parameter => parameter.Name).ToArray();

        CollectionAssert.DoesNotContain(names, "requestedDepth");
        CollectionAssert.DoesNotContain(names, "minimumSliceBytes");
        CollectionAssert.Contains(names, "data");
        CollectionAssert.Contains(names, "scheduler");
    }

    [TestMethod]
    public void DirectWriterDoesNotExposeIntraBlockDepthOrSliceControls()
    {
        var type = typeof(CopyEngine).Assembly.GetType("RepartoCopier.Core.DirectIoDestinationWriter+Session", throwOnError: true)!;
        var write = type.GetMethod("WriteAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("DirectIoDestinationWriter.Session.WriteAsync no existe.");
        var names = write.GetParameters().Select(parameter => parameter.Name).ToArray();

        CollectionAssert.DoesNotContain(names, "requestedDepth");
        CollectionAssert.DoesNotContain(names, "minimumSliceBytes");
    }
}
'@ | Set-Content -LiteralPath $contractPath -Encoding utf8

# Exhaustive architecture grep: the old intra-block controls must have no production consumers.
$matches = Get-ChildItem dotnet -Recurse -File -Filter *.cs | Select-String -Pattern 'DestinationWriteCoordinator\.WriteAsync|requestedDepth|minimumSliceBytes'
$unexpected = $matches | Where-Object {
    $_.Path -notlike '*SequentialBlockWriteArchitectureTests.cs' -and
    $_.Line -match 'requestedDepth|minimumSliceBytes'
}
if ($unexpected) {
    $unexpected | ForEach-Object { Write-Host "$($_.Path):$($_.LineNumber):$($_.Line)" }
    throw 'Legacy intra-block depth/slice controls remain in dotnet/.'
}
