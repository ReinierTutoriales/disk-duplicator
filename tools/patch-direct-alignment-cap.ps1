$ErrorActionPreference = 'Stop'

$sourcePath = 'dotnet/RepartoCopier.Core/DirectIoSourceReader.cs'
$destinationPath = 'dotnet/RepartoCopier.Core/DirectIoDestinationWriter.cs'
$verifyPath = 'dotnet/RepartoCopier.Core/FastVerificationReader.cs'
$corePath = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$sourceTestsPath = 'dotnet/RepartoCopier.Core.Tests/DirectIoSourceReaderTests.cs'
$freedomTestsPath = 'dotnet/RepartoCopier.Core.Tests/PerformanceFreedomArchitectureTests.cs'
$contractPath = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'
$roadmapPath = 'docs/FANOUT-PERFORMANCE-ROADMAP.md'

$source = Get-Content -LiteralPath $sourcePath -Raw
$source = $source.Replace("`r`n    internal const int MaximumSupportedAlignment = 64 * 1024;`r`n", "`r`n")
$oldEligibility = @'
        var alignment = RequiredAlignment(device);
        return alignment is >= 512 and <= MaximumSupportedAlignment &&
               IsPowerOfTwo(alignment) &&
               transferSize % alignment == 0;
'@
$newEligibility = @'
        var alignment = RequiredAlignment(device);
        return alignment >= 512 &&
               IsPowerOfTwo(alignment) &&
               transferSize % alignment == 0;
'@
if (-not $source.Contains($oldEligibility)) { throw 'Source alignment eligibility block not found.' }
$source = $source.Replace($oldEligibility, $newEligibility)
$oldAlignmentProperty = @'
            var pointer = Pointer.ToInt64();
            var alignment = 1;
            while (alignment < DirectIoSourceReader.MaximumSupportedAlignment && pointer % (alignment * 2L) == 0)
                alignment *= 2;
            return alignment;
'@
$newAlignmentProperty = @'
            var pointer = Pointer.ToInt64();
            var alignment = 1;
            while (alignment <= int.MaxValue / 2 && pointer % (alignment * 2L) == 0)
                alignment *= 2;
            return alignment;
'@
if (-not $source.Contains($oldAlignmentProperty)) { throw 'SourceBufferLease alignment property not found.' }
$source = $source.Replace($oldAlignmentProperty, $newAlignmentProperty)
Set-Content -LiteralPath $sourcePath -Value $source -Encoding utf8

$destination = Get-Content -LiteralPath $destinationPath -Raw
$oldDestinationEligibility = '        return alignment is >= 512 and <= 64 * 1024 && IsPowerOfTwo(alignment);'
$newDestinationEligibility = '        return alignment >= 512 && IsPowerOfTwo(alignment);'
if (-not $destination.Contains($oldDestinationEligibility)) { throw 'Destination alignment cap not found.' }
$destination = $destination.Replace($oldDestinationEligibility, $newDestinationEligibility)
Set-Content -LiteralPath $destinationPath -Value $destination -Encoding utf8

$verify = Get-Content -LiteralPath $verifyPath -Raw
$oldVerifyOpen = @'
        if (DirectIoSourceReader.TryOpenOverlappedForVerification(
                path,
                device,
                DirectIoSourceReader.MaximumSupportedAlignment,
                out var direct))
'@
$newVerifyOpen = @'
        var directAlignment = DirectIoSourceReader.RequiredAlignment(device);
        if (DirectIoSourceReader.TryOpenOverlappedForVerification(
                path,
                device,
                Math.Max(1, directAlignment),
                out var direct))
'@
if (-not $verify.Contains($oldVerifyOpen)) { throw 'Verification direct open block not found.' }
$verify = $verify.Replace($oldVerifyOpen, $newVerifyOpen)
Set-Content -LiteralPath $verifyPath -Value $verify -Encoding utf8

$core = Get-Content -LiteralPath $corePath -Raw
$core = $core.Replace(
@'
                        active,
                        readBufferSize,
                        bufferBudget,
'@,
@'
                        active,
                        readBufferSize,
                        transferAlignment,
                        bufferBudget,
'@)

$oldSequentialSig = @'
        List<DestinationWorker> active,
        int readBufferSize,
        AdaptiveByteBudget bufferBudget,
'@
$newSequentialSig = @'
        List<DestinationWorker> active,
        int readBufferSize,
        int transferAlignment,
        AdaptiveByteBudget bufferBudget,
'@
$sequentialStart = $core.IndexOf('    private static async Task<SourceReadResult?> ReadAndFanOutSequentialAsync(')
if ($sequentialStart -lt 0) { throw 'Sequential reader not found.' }
$sequentialSigIndex = $core.IndexOf($oldSequentialSig, $sequentialStart)
if ($sequentialSigIndex -lt 0) { throw 'Sequential signature alignment anchor not found.' }
$core = $core.Remove($sequentialSigIndex, $oldSequentialSig.Length).Insert($sequentialSigIndex, $newSequentialSig)

$prefetchedStart = $core.IndexOf('    private static async Task<SourceReadResult?> ReadAndFanOutPrefetchedAsync(')
if ($prefetchedStart -lt 0) { throw 'Prefetched reader not found.' }
$prefetchedSigIndex = $core.IndexOf($oldSequentialSig, $prefetchedStart)
if ($prefetchedSigIndex -lt 0) { throw 'Prefetched signature alignment anchor not found.' }
$core = $core.Remove($prefetchedSigIndex, $oldSequentialSig.Length).Insert($prefetchedSigIndex, $newSequentialSig)

$oldPrefetchCall = @'
            sourceDevice,
            readBufferSize,
            sourceQueue.Writer,
'@
$newPrefetchCall = @'
            sourceDevice,
            readBufferSize,
            transferAlignment,
            sourceQueue.Writer,
'@
if (-not $core.Contains($oldPrefetchCall)) { throw 'PrefetchSource call alignment anchor not found.' }
$core = $core.Replace($oldPrefetchCall, $newPrefetchCall)

$oldPrefetchSig = @'
        StorageDeviceInfo sourceDevice,
        int readBufferSize,
        ChannelWriter<SourceReadBlock> output,
'@
$newPrefetchSig = @'
        StorageDeviceInfo sourceDevice,
        int readBufferSize,
        int transferAlignment,
        ChannelWriter<SourceReadBlock> output,
'@
$prefetchSourceStart = $core.IndexOf('    private static async Task<SourceReadResult> PrefetchSourceAsync(')
if ($prefetchSourceStart -lt 0) { throw 'PrefetchSourceAsync not found.' }
$prefetchSourceSigIndex = $core.IndexOf($oldPrefetchSig, $prefetchSourceStart)
if ($prefetchSourceSigIndex -lt 0) { throw 'PrefetchSource signature alignment anchor not found.' }
$core = $core.Remove($prefetchSourceSigIndex, $oldPrefetchSig.Length).Insert($prefetchSourceSigIndex, $newPrefetchSig)

$oldAheadCall = @'
            sourceDevice,
            readBufferSize,
            hashQueue.Writer,
'@
$newAheadCall = @'
            sourceDevice,
            readBufferSize,
            transferAlignment,
            hashQueue.Writer,
'@
if (-not $core.Contains($oldAheadCall)) { throw 'ReadSourceAhead call alignment anchor not found.' }
$core = $core.Replace($oldAheadCall, $newAheadCall)

$readAheadStart = $core.IndexOf('    private static async Task<long> ReadSourceAheadAsync(')
if ($readAheadStart -lt 0) { throw 'ReadSourceAheadAsync not found.' }
$readAheadSigIndex = $core.IndexOf($oldPrefetchSig, $readAheadStart)
if ($readAheadSigIndex -lt 0) { throw 'ReadSourceAhead signature alignment anchor not found.' }
$core = $core.Remove($readAheadSigIndex, $oldPrefetchSig.Length).Insert($readAheadSigIndex, $newPrefetchSig)

$core = $core.Replace(
@'
                SourceBufferLease? lease = SourceBufferLease.RentAligned(
                    readBufferSize,
                    DirectIoSourceReader.MaximumSupportedAlignment);
'@,
@'
                SourceBufferLease? lease = SourceBufferLease.RentAligned(
                    readBufferSize,
                    transferAlignment);
'@)
$core = $core.Replace(
@'
                    lease = SourceBufferLease.RentAligned(
                        readBufferSize,
                        DirectIoSourceReader.MaximumSupportedAlignment);
'@,
@'
                    lease = SourceBufferLease.RentAligned(
                        readBufferSize,
                        transferAlignment);
'@)
$core = $core.Replace(
@'
                                lease = SourceBufferLease.RentAligned(
                                    readBufferSize,
                                    DirectIoSourceReader.MaximumSupportedAlignment);
'@,
@'
                                lease = SourceBufferLease.RentAligned(
                                    readBufferSize,
                                    transferAlignment);
'@)

$oldReplayRent = @'
        SourceBufferLease? lease = SourceBufferLease.RentAligned(
            segment.Length,
            DirectIoSourceReader.MaximumSupportedAlignment);
'@
$newReplayRent = @'
        SourceBufferLease? lease = SourceBufferLease.RentAligned(
            segment.Length,
            BufferAlignmentFor(worker.Device));
'@
if (-not $core.Contains($oldReplayRent)) { throw 'Replay alignment rent not found.' }
$core = $core.Replace($oldReplayRent, $newReplayRent)

$transferHelperAnchor = @'
    private static int TransferAlignmentFor(
        StorageDeviceInfo source,
        IReadOnlyList<DestinationWorker> active)
'@
$bufferHelper = @'
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

'@
if (-not $core.Contains($transferHelperAnchor)) { throw 'TransferAlignmentFor helper not found.' }
$core = $core.Replace($transferHelperAnchor, $bufferHelper + $transferHelperAnchor)

if ($core.Contains('MaximumSupportedAlignment')) { throw 'MaximumSupportedAlignment remains in CopyEngine.' }
Set-Content -LiteralPath $corePath -Value $core -Encoding utf8

$sourceTests = Get-Content -LiteralPath $sourceTestsPath -Raw
$testAnchor = @'
    [TestMethod]
    public void NetworkAndMisalignedTransfersFallBackButUncertainLocalIdentityCanAttemptDirectIo()
'@
$highAlignmentTest = @'
    [TestMethod]
    public void AlignmentAboveSixtyFourKiBIsDerivedFromDeviceNotArtificiallyRejected()
    {
        const int alignment = 128 * 1024;
        var device = Device("NVMe", StorageMediaKind.SolidState, 4096, alignment);

        Assert.AreEqual(alignment, DirectIoSourceReader.RequiredAlignment(device));
        Assert.IsTrue(DirectIoSourceReader.IsEligible(device, 2 * alignment));
        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(device, 1));
        using var lease = SourceBufferLease.RentAligned(2 * alignment, alignment);
        Assert.IsTrue(lease.IsAlignedFor(alignment));
    }

'@
if (-not $sourceTests.Contains($testAnchor)) { throw 'Direct source high-alignment test anchor not found.' }
$sourceTests = $sourceTests.Replace($testAnchor, $highAlignmentTest + $testAnchor)
Set-Content -LiteralPath $sourceTestsPath -Value $sourceTests -Encoding utf8

$freedom = Get-Content -LiteralPath $freedomTestsPath -Raw
$oldFreedomRent = '        using var payload = SourceBufferLease.RentAligned(1024 * 1024, DirectIoSourceReader.MaximumSupportedAlignment);'
$newFreedomRent = '        using var payload = SourceBufferLease.RentAligned(1024 * 1024, 128 * 1024);'
if (-not $freedom.Contains($oldFreedomRent)) { throw 'Performance freedom alignment test anchor not found.' }
$freedom = $freedom.Replace($oldFreedomRent, $newFreedomRent)
$freedom = $freedom.Replace('        Assert.IsTrue(payload.IsAlignedFor(64 * 1024));', '        Assert.IsTrue(payload.IsAlignedFor(128 * 1024));')
Set-Content -LiteralPath $freedomTestsPath -Value $freedom -Encoding utf8

$contract = Get-Content -LiteralPath $contractPath -Raw
$contractAnchor = @'
    [TestMethod]
    public void AdaptiveTransferSizingReplacesFixedReadBands()
'@
$contractInsert = @'
    [TestMethod]
    public void DirectIoAlignmentHasNoSixtyFourKiBCap()
    {
        var field = typeof(DirectIoSourceReader).GetField(
            "MaximumSupportedAlignment",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNull(field);

        var sourceFields = typeof(DirectIoSourceReader)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(item => item.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(sourceFields, "MaximumSupportedAlignment");
    }

'@
if (-not $contract.Contains($contractAnchor)) { throw 'Alignment architecture contract anchor not found.' }
$contract = $contract.Replace($contractAnchor, $contractInsert + $contractAnchor)
Set-Content -LiteralPath $contractPath -Value $contract -Encoding utf8

$roadmap = Get-Content -LiteralPath $roadmapPath -Raw
$oldRoadmap = @'
### P2 — alineación máxima Direct I/O

El soporte de payload alineado mantiene `MaximumSupportedAlignment = 64 KiB`. Auditar si puede derivarse completamente del dispositivo sin máximo de implementación fijo.

'@
$newRoadmap = @'
### VALIDACIÓN FÍSICA — alineación Direct I/O

El techo artificial de 64 KiB fue eliminado de source, destination y verify. La elegibilidad Direct I/O ahora acepta cualquier alineación de sector conocida >=512 que sea potencia de dos y compatible con el tamaño de transferencia. Los buffers FAN-OUT reciben la alineación máxima real de los dispositivos participantes; replay usa la alineación del destino y verification abre Direct con la alineación requerida por ese dispositivo. Existe contrato sintético de 128 KiB. Pendiente únicamente validación con hardware real que reporte alineaciones superiores a 64 KiB.

'@
if (-not $roadmap.Contains($oldRoadmap)) { throw 'Alignment roadmap block not found.' }
$roadmap = $roadmap.Replace($oldRoadmap, $newRoadmap)
Set-Content -LiteralPath $roadmapPath -Value $roadmap -Encoding utf8

git diff --check
git status --short
