$ErrorActionPreference = 'Stop'

$corePath = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$telemetryPath = 'dotnet/RepartoCopier.Core/CopyTelemetry.cs'
$policyPath = 'dotnet/RepartoCopier.Core/AdaptiveTransferSizer.cs'
$testPath = 'dotnet/RepartoCopier.Core.Tests/SourceWindowPerformanceTests.cs'
$contractPath = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'
$roadmapPath = 'docs/FANOUT-PERFORMANCE-ROADMAP.md'

@'
namespace RepartoCopier.Core;

internal readonly record struct TransferDeviceSignal(
    int CurrentQueueDepth,
    int BestQueueDepth,
    double BestThroughputBytesPerSecond,
    double BestAverageLatencyMilliseconds);

/// <summary>
/// Chooses the source transfer size from current memory headroom, destination
/// concurrency and measured physical I/O. There are no file-size bands and no
/// fixed large-block target. Before physical feedback exists, memory headroom is
/// distributed across the blocks needed to sustain current QD/prefetch demand.
/// Once throughput/latency feedback exists, Little's-law bytes-per-operation is
/// used as an evidence-based target, still bounded by current memory pressure.
/// </summary>
internal static class AdaptiveTransferSizer
{
    internal static int Select(
        long fileSize,
        int activeDestinations,
        int requiredAlignment,
        long bufferTargetBytes,
        long bufferUsedBytes,
        int currentPrefetchLimit,
        IReadOnlyList<TransferDeviceSignal> devices)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(activeDestinations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requiredAlignment);
        if ((requiredAlignment & (requiredAlignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(requiredAlignment));
        ArgumentOutOfRangeException.ThrowIfNegative(bufferTargetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferUsedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(currentPrefetchLimit);
        ArgumentNullException.ThrowIfNull(devices);

        var headroom = Math.Max(
            (long)requiredAlignment,
            Math.Max(0L, bufferTargetBytes - bufferUsedBytes));
        var maximumQd = devices.Count == 0
            ? 1
            : devices.Max(item => Math.Max(1, item.CurrentQueueDepth));
        var residentBlocks = checked(
            Math.Max(currentPrefetchLimit, maximumQd) + Math.Max(0, activeDestinations - 1));
        var memoryBound = Math.Max(
            (long)requiredAlignment,
            headroom / Math.Max(1, residentBlocks));

        double measuredBytesPerOperation = 0;
        var measuredCount = 0;
        foreach (var device in devices)
        {
            if (device.BestThroughputBytesPerSecond <= 0 ||
                device.BestAverageLatencyMilliseconds <= 0)
            {
                continue;
            }

            var qd = Math.Max(1, device.BestQueueDepth);
            var bytes = device.BestThroughputBytesPerSecond *
                (device.BestAverageLatencyMilliseconds / 1000.0) / qd;
            if (!double.IsFinite(bytes) || bytes <= 0)
                continue;
            measuredBytesPerOperation += bytes;
            measuredCount++;
        }

        long candidate = memoryBound;
        if (measuredCount > 0)
        {
            var measured = (long)Math.Max(
                requiredAlignment,
                measuredBytesPerOperation / measuredCount);
            candidate = Math.Min(memoryBound, measured);
        }

        var maxIntTransfer = (long)int.MaxValue - requiredAlignment;
        candidate = Math.Min(candidate, Math.Max(requiredAlignment, maxIntTransfer));
        if (fileSize > 0)
            candidate = Math.Min(candidate, Math.Max((long)requiredAlignment, fileSize));

        var aligned = candidate - candidate % requiredAlignment;
        if (aligned < requiredAlignment)
            aligned = requiredAlignment;
        return checked((int)aligned);
    }
}
'@ | Set-Content -LiteralPath $policyPath -Encoding utf8

@'
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SourceWindowPerformanceTests
{
    [TestMethod]
    public void TransferSizeRespondsToMemoryPressureAndDestinationCount()
    {
        var signals = new[] { new TransferDeviceSignal(8, 8, 0, 0) };

        var roomy = AdaptiveTransferSizer.Select(
            4L * 1024 * 1024 * 1024,
            activeDestinations: 1,
            requiredAlignment: 4096,
            bufferTargetBytes: 512L * 1024 * 1024,
            bufferUsedBytes: 0,
            currentPrefetchLimit: 4,
            signals);
        var pressured = AdaptiveTransferSizer.Select(
            4L * 1024 * 1024 * 1024,
            activeDestinations: 4,
            requiredAlignment: 4096,
            bufferTargetBytes: 512L * 1024 * 1024,
            bufferUsedBytes: 384L * 1024 * 1024,
            currentPrefetchLimit: 4,
            signals);

        Assert.IsGreaterThan(pressured, 0);
        Assert.IsGreaterThan(pressured, 4095);
        Assert.IsGreaterThan(roomy, pressured);
        Assert.AreEqual(0, roomy % 4096);
        Assert.AreEqual(0, pressured % 4096);
    }

    [TestMethod]
    public void MeasuredThroughputAndLatencyCanReduceOperationSize()
    {
        var noFeedback = new[] { new TransferDeviceSignal(16, 16, 0, 0) };
        var feedback = new[]
        {
            new TransferDeviceSignal(
                16,
                16,
                512d * 1024 * 1024,
                64d),
        };

        var initial = AdaptiveTransferSizer.Select(
            2L * 1024 * 1024 * 1024,
            1,
            4096,
            512L * 1024 * 1024,
            0,
            4,
            noFeedback);
        var measured = AdaptiveTransferSizer.Select(
            2L * 1024 * 1024 * 1024,
            1,
            4096,
            512L * 1024 * 1024,
            0,
            4,
            feedback);

        Assert.IsLessThan(measured, initial);
        Assert.AreEqual(0, measured % 4096);
    }

    [TestMethod]
    public void CopyEngineNoLongerExposesFixedReadBufferBands()
    {
        var method = typeof(CopyEngine).GetMethod(
            "ReadBufferSizeFor",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNull(method);

        var fields = typeof(CopyEngine)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Select(field => field.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(fields, "BlockSize");
        CollectionAssert.DoesNotContain(fields, "SmallBufferSize");
        CollectionAssert.DoesNotContain(fields, "MediumBufferSize");
        CollectionAssert.DoesNotContain(fields, "LargeBufferSize");
    }

    [TestMethod]
    public void PipelineGovernorTracksChangedTransferSize()
    {
        const long oneGiB = 1024L * 1024 * 1024;
        var budget = new CopyEngine.AdaptiveByteBudget(512L * 1024 * 1024, oneGiB);
        var governor = new CopyEngine.PipelineGovernor(budget, 32 * 1024 * 1024);
        var before = governor.Snapshot().CurrentPrefetchLimit;

        governor.SetBytesPerBlock(8 * 1024 * 1024);
        for (var decision = 0; decision < 4; decision++)
        {
            for (var sample = 0; sample < 8; sample++)
                governor.RecordConsumerWait(TimeSpan.FromMilliseconds(10));
        }

        Assert.IsGreaterThanOrEqualTo(governor.Snapshot().CurrentPrefetchLimit, before);
    }
}
'@ | Set-Content -LiteralPath $testPath -Encoding utf8

$core = Get-Content -LiteralPath $corePath -Raw

$oldConstants = @'
    private const int BlockSize = 32 * 1024 * 1024;

    private const int SmallBufferSize = 64 * 1024;
    private const int MediumBufferSize = 1024 * 1024;
    private const int LargeBufferSize = 4 * 1024 * 1024;

'@
if (-not $core.Contains($oldConstants)) { throw 'Fixed transfer constants not found.' }
$core = $core.Replace($oldConstants, '')

$oldPipeline = '        var pipeline = new PipelineGovernor(bufferBudget, BlockSize);'
$newPipeline = '        var pipeline = new PipelineGovernor(bufferBudget, Math.Max(1, Environment.SystemPageSize));'
if (-not $core.Contains($oldPipeline)) { throw 'Pipeline initialization not found.' }
$core = $core.Replace($oldPipeline, $newPipeline)

$oldSelection = @'
                var readBufferSize = ReadBufferSizeFor(entry.Size);
                var sourceResult = entry.Size > readBufferSize
                    ? await ReadAndFanOutPrefetchedAsync(
                        entry,
                        copy.SourceDevice,
                        active,
                        bufferBudget,
                        job,
                        pipeline,
                        sharedSourceScheduler).ConfigureAwait(false)
                    : await ReadAndFanOutSequentialAsync(
                        entry,
                        copy.SourceDevice,
                        active,
                        bufferBudget,
                        job,
                        pipeline,
                        sharedSourceScheduler).ConfigureAwait(false);
'@
$newSelection = @'
                var transferAlignment = TransferAlignmentFor(copy.SourceDevice, active);
                var signals = active
                    .Select(worker => worker.DeviceScheduler.Snapshot())
                    .Select(snapshot => new TransferDeviceSignal(
                        snapshot.CurrentQueueDepth,
                        snapshot.BestObservedQueueDepth,
                        snapshot.BestObservedThroughputBytesPerSecond,
                        snapshot.BestObservedAverageLatencyMilliseconds))
                    .ToArray();
                var readBufferSize = AdaptiveTransferSizer.Select(
                    entry.Size,
                    active.Count,
                    transferAlignment,
                    bufferBudget.TargetBytes,
                    bufferBudget.UsedBytes,
                    Math.Max(1, pipeline.Snapshot().CurrentPrefetchLimit),
                    signals);
                pipeline.SetBytesPerBlock(readBufferSize);
                job.Telemetry.RecordTransferSize(readBufferSize);

                var sourceResult = entry.Size > readBufferSize
                    ? await ReadAndFanOutPrefetchedAsync(
                        entry,
                        copy.SourceDevice,
                        active,
                        readBufferSize,
                        bufferBudget,
                        job,
                        pipeline,
                        sharedSourceScheduler).ConfigureAwait(false)
                    : await ReadAndFanOutSequentialAsync(
                        entry,
                        copy.SourceDevice,
                        active,
                        readBufferSize,
                        bufferBudget,
                        job,
                        pipeline,
                        sharedSourceScheduler).ConfigureAwait(false);
'@
if (-not $core.Contains($oldSelection)) { throw 'Producer transfer selection not found.' }
$core = $core.Replace($oldSelection, $newSelection)

$oldSequentialSig = @'
        StorageDeviceInfo sourceDevice,
        List<DestinationWorker> active,
        AdaptiveByteBudget bufferBudget,
'@
$newSequentialSig = @'
        StorageDeviceInfo sourceDevice,
        List<DestinationWorker> active,
        int readBufferSize,
        AdaptiveByteBudget bufferBudget,
'@
$firstSig = $core.IndexOf('    private static async Task<SourceReadResult?> ReadAndFanOutSequentialAsync(')
if ($firstSig -lt 0) { throw 'Sequential method not found.' }
$sigIndex = $core.IndexOf($oldSequentialSig, $firstSig)
if ($sigIndex -lt 0) { throw 'Sequential signature block not found.' }
$core = $core.Remove($sigIndex, $oldSequentialSig.Length).Insert($sigIndex, $newSequentialSig)
$seqLocal = '        var readBufferSize = ReadBufferSizeFor(entry.Size);' + "`r`n"
$seqLocalIndex = $core.IndexOf($seqLocal, $firstSig)
if ($seqLocalIndex -lt 0) { throw 'Sequential fixed size local not found.' }
$core = $core.Remove($seqLocalIndex, $seqLocal.Length)

$prefetchStart = $core.IndexOf('    private static async Task<SourceReadResult?> ReadAndFanOutPrefetchedAsync(')
if ($prefetchStart -lt 0) { throw 'Prefetched method not found.' }
$prefetchSigIndex = $core.IndexOf($oldSequentialSig, $prefetchStart)
if ($prefetchSigIndex -lt 0) { throw 'Prefetched signature block not found.' }
$core = $core.Remove($prefetchSigIndex, $oldSequentialSig.Length).Insert($prefetchSigIndex, $newSequentialSig)
$core = $core.Replace(
'            ReadBufferSizeFor(entry.Size),',
'            readBufferSize,')

$oldReadMethod = @'
    private static int ReadBufferSizeFor(long fileSize) =>
        fileSize <= SmallBufferSize ? SmallBufferSize :
        fileSize <= MediumBufferSize ? MediumBufferSize :
        fileSize <= LargeBufferSize ? LargeBufferSize :
        BlockSize;

'@
if (-not $core.Contains($oldReadMethod)) { throw 'ReadBufferSizeFor not found.' }
$core = $core.Replace($oldReadMethod, '')

$pathKeyAnchor = '    private static string PathKey(string relative) => relative.Replace(''/'' , ''\'');'
# Use the stable ToUnixNanoseconds line instead of relying on slash escaping.
$timeAnchor = @'
    private static long ToUnixNanoseconds(DateTime utc) =>
        checked((utc.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks) * 100L);

'@
$alignmentHelper = @'
    private static int TransferAlignmentFor(
        StorageDeviceInfo source,
        IReadOnlyList<DestinationWorker> active)
    {
        var alignment = Math.Max(1, Environment.SystemPageSize);
        if (source.HasKnownSectorAlignment)
        {
            var sourceAlignment = DirectIoSourceReader.RequiredAlignment(source);
            if (sourceAlignment > 0 && (sourceAlignment & (sourceAlignment - 1)) == 0)
                alignment = Math.Max(alignment, sourceAlignment);
        }
        foreach (var worker in active)
        {
            if (!worker.Device.HasKnownSectorAlignment)
                continue;
            var destinationAlignment = DirectIoSourceReader.RequiredAlignment(worker.Device);
            if (destinationAlignment > 0 && (destinationAlignment & (destinationAlignment - 1)) == 0)
                alignment = Math.Max(alignment, destinationAlignment);
        }
        return alignment;
    }

'@
if (-not $core.Contains($timeAnchor)) { throw 'Transfer alignment insertion anchor not found.' }
$core = $core.Replace($timeAnchor, $timeAnchor + $alignmentHelper)

$core = $core.Replace(
'        private readonly int _bytesPerBlock;',
'        private int _bytesPerBlock;')
$pipelineMethodAnchor = @'
        public ValueTask AcquirePrefetchSlotAsync(CancellationToken token)
'@
$setBlockMethod = @'
        internal void SetBytesPerBlock(int bytesPerBlock)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerBlock);
            lock (_gate)
            {
                _bytesPerBlock = bytesPerBlock;
                var capacity = CurrentCapacityLocked();
                if (_prefetchLimit > capacity)
                    _prefetchLimit = capacity;
                PumpSlotsLocked();
            }
        }

'@
if (-not $core.Contains($pipelineMethodAnchor)) { throw 'Pipeline SetBytes anchor not found.' }
$core = $core.Replace($pipelineMethodAnchor, $setBlockMethod + $pipelineMethodAnchor)

$core = $core.Replace(
'        private readonly Func<long, long> _capacityProvider;' + "`r`n" + '        private long _targetBytes;',
'        private readonly Func<long, long> _capacityProvider;' + "`r`n" + '        private readonly bool _usesSystemCapacity;' + "`r`n" + '        private long _targetBytes;')

$oldBudgetCtors = @'
        internal AdaptiveByteBudget(long initialBytes, long maximumBytes)
            : this(initialBytes, _ => maximumBytes)
        {
            if (maximumBytes <= 0 || initialBytes <= 0 || initialBytes > maximumBytes)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        private AdaptiveByteBudget(long initialBytes, Func<long, long> capacityProvider)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialBytes);
            _capacityProvider = capacityProvider ?? throw new ArgumentNullException(nameof(capacityProvider));
            _targetBytes = initialBytes;
        }

        public static AdaptiveByteBudget CreateForSystem()
        {
            var safeNow = GetSystemSafeCapacity(0);
            var initial = Math.Max((long)BlockSize, Math.Min(InitialBufferBudget, safeNow));
            return new AdaptiveByteBudget(initial, GetSystemSafeCapacity);
        }
'@
$newBudgetCtors = @'
        internal AdaptiveByteBudget(long initialBytes, long maximumBytes)
            : this(initialBytes, _ => maximumBytes, usesSystemCapacity: false)
        {
            if (maximumBytes <= 0 || initialBytes <= 0 || initialBytes > maximumBytes)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        private AdaptiveByteBudget(
            long initialBytes,
            Func<long, long> capacityProvider,
            bool usesSystemCapacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialBytes);
            _capacityProvider = capacityProvider ?? throw new ArgumentNullException(nameof(capacityProvider));
            _usesSystemCapacity = usesSystemCapacity;
            _targetBytes = initialBytes;
        }

        public static AdaptiveByteBudget CreateForSystem()
        {
            var progressQuantum = Math.Max(1, Environment.SystemPageSize);
            var safeNow = MemoryPressureCapacity.GetSafeTotalBytes(0, progressQuantum);
            var initial = Math.Max((long)progressQuantum, Math.Min(InitialBufferBudget, safeNow));
            return new AdaptiveByteBudget(initial, _ => 0, usesSystemCapacity: true);
        }
'@
if (-not $core.Contains($oldBudgetCtors)) { throw 'AdaptiveByteBudget constructor block not found.' }
$core = $core.Replace($oldBudgetCtors, $newBudgetCtors)
$core = $core.Replace(
'                var capacity = CurrentSafeCapacityLocked();',
'                var capacity = CurrentSafeCapacityLocked(bytesPerBlock);')
$core = $core.Replace(
'            var safeCapacity = CurrentSafeCapacityLocked();',
'            var safeCapacity = CurrentSafeCapacityLocked(bytes);')
$oldSafeCapacity = @'
        private long CurrentSafeCapacityLocked()
        {
            var capacity = _capacityProvider(_usedBytes);
            return Math.Max(_usedBytes, capacity);
        }

        private static long GetSystemSafeCapacity(long usedBytes) =>
            MemoryPressureCapacity.GetSafeTotalBytes(usedBytes, BlockSize);
'@
$newSafeCapacity = @'
        private long CurrentSafeCapacityLocked(int minimumProgressBytes)
        {
            var capacity = _usesSystemCapacity
                ? MemoryPressureCapacity.GetSafeTotalBytes(_usedBytes, minimumProgressBytes)
                : _capacityProvider(_usedBytes);
            return Math.Max(_usedBytes, capacity);
        }
'@
if (-not $core.Contains($oldSafeCapacity)) { throw 'AdaptiveByteBudget safe capacity block not found.' }
$core = $core.Replace($oldSafeCapacity, $newSafeCapacity)

Set-Content -LiteralPath $corePath -Value $core -Encoding utf8

$telemetry = Get-Content -LiteralPath $telemetryPath -Raw
$propertyAnchor = '    public long BranchReplaySegments { get; init; }'
$properties = @'
    public long BranchReplaySegments { get; init; }
    public int CurrentTransferBytes { get; init; }
    public int MinimumTransferBytes { get; init; }
    public int MaximumTransferBytes { get; init; }
'@
if (-not $telemetry.Contains($propertyAnchor)) { throw 'Transfer telemetry property anchor not found.' }
$telemetry = $telemetry.Replace($propertyAnchor, $properties.TrimEnd())
$fieldAnchor = '    private long _branchReplayReadBytes, _branchReplayReadTicks, _branchReplaySegments;'
$fieldReplacement = @'
    private long _branchReplayReadBytes, _branchReplayReadTicks, _branchReplaySegments;
    private int _currentTransferBytes, _minimumTransferBytes = int.MaxValue, _maximumTransferBytes;
'@
if (-not $telemetry.Contains($fieldAnchor)) { throw 'Transfer telemetry field anchor not found.' }
$telemetry = $telemetry.Replace($fieldAnchor, $fieldReplacement.TrimEnd())
$methodAnchor = '    internal void RecordCopyPhase(TimeSpan elapsed) => AddTicks(ref _copyPhaseTicks, elapsed);'
$methods = @'
    internal void RecordTransferSize(int bytes)
    {
        if (bytes <= 0) return;
        Volatile.Write(ref _currentTransferBytes, bytes);
        UpdateMin(ref _minimumTransferBytes, bytes);
        UpdateMax(ref _maximumTransferBytes, bytes);
    }
    internal void RecordCopyPhase(TimeSpan elapsed) => AddTicks(ref _copyPhaseTicks, elapsed);
'@
if (-not $telemetry.Contains($methodAnchor)) { throw 'Transfer telemetry method anchor not found.' }
$telemetry = $telemetry.Replace($methodAnchor, $methods.TrimEnd())
$initAnchor = '            BranchReplaySegments = Interlocked.Read(ref _branchReplaySegments),'
$initReplacement = @'
            BranchReplaySegments = Interlocked.Read(ref _branchReplaySegments),
            CurrentTransferBytes = Volatile.Read(ref _currentTransferBytes),
            MinimumTransferBytes = Volatile.Read(ref _minimumTransferBytes) == int.MaxValue ? 0 : Volatile.Read(ref _minimumTransferBytes),
            MaximumTransferBytes = Volatile.Read(ref _maximumTransferBytes),
'@
if (-not $telemetry.Contains($initAnchor)) { throw 'Transfer telemetry snapshot anchor not found.' }
$telemetry = $telemetry.Replace($initAnchor, $initReplacement.TrimEnd())
$maxAnchor = '    private static void UpdateMax(ref long target, long value)'
$minMethod = @'
    private static void UpdateMin(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value < current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

'@
if (-not $telemetry.Contains($maxAnchor)) { throw 'Telemetry UpdateMin insertion anchor not found.' }
$telemetry = $telemetry.Replace($maxAnchor, $minMethod + $maxAnchor)
# Existing UpdateMax is long-only; add an int overload.
$telemetry = $telemetry.Replace(
'    private static void UpdateMax(ref long target, long value)',
'    private static void UpdateMax(ref int target, int value)' + "`r`n" + '    {' + "`r`n" + '        var current = Volatile.Read(ref target);' + "`r`n" + '        while (value > current)' + "`r`n" + '        {' + "`r`n" + '            var observed = Interlocked.CompareExchange(ref target, value, current);' + "`r`n" + '            if (observed == current) return;' + "`r`n" + '            current = observed;' + "`r`n" + '        }' + "`r`n" + '    }' + "`r`n`r`n" + '    private static void UpdateMax(ref long target, long value)')
Set-Content -LiteralPath $telemetryPath -Value $telemetry -Encoding utf8

$contract = Get-Content -LiteralPath $contractPath -Raw
$anchor = @'
    [TestMethod]
    public void SlowBranchReplayIsARealProductionPath()
'@
$insert = @'
    [TestMethod]
    public void AdaptiveTransferSizingReplacesFixedReadBands()
    {
        var assembly = typeof(CopyEngine).Assembly;
        Assert.IsNotNull(assembly.GetType("RepartoCopier.Core.AdaptiveTransferSizer"));
        Assert.IsNull(typeof(CopyEngine).GetMethod(
            "ReadBufferSizeFor",
            BindingFlags.Static | BindingFlags.NonPublic));
        var fields = typeof(CopyEngine)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(field => field.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(fields, "BlockSize");
        CollectionAssert.DoesNotContain(fields, "SmallBufferSize");
        CollectionAssert.DoesNotContain(fields, "MediumBufferSize");
        CollectionAssert.DoesNotContain(fields, "LargeBufferSize");
    }

'@
if (-not $contract.Contains($anchor)) { throw 'Adaptive transfer contract anchor not found.' }
$contract = $contract.Replace($anchor, $insert + $anchor)
Set-Content -LiteralPath $contractPath -Value $contract -Encoding utf8

$roadmap = Get-Content -LiteralPath $roadmapPath -Raw
$old = @'
### P1 — BlockSize / ventana de lectura adaptativos

`BlockSize` continúa fijo en 32 MiB y `ReadBufferSizeFor` usa bandas 64 KiB / 1 MiB / 4 MiB / 32 MiB. Son heurísticas pendientes de demostrar. La siguiente evolución debe explorar tamaño de bloque/ventana según throughput, latencia, QD, número de destinos y presión de memoria. No sustituir 32 MiB por otro número fijo.

'@
$new = @'
### VALIDACIÓN FÍSICA — tamaño de transferencia / ventana de lectura

El `BlockSize` fijo y las bandas de `ReadBufferSizeFor` fueron eliminados. `AdaptiveTransferSizer` calcula el tamaño por archivo usando headroom actual de `AdaptiveByteBudget`, destinos activos, QD físico, límite de prefetch y, cuando ya existe feedback, throughput + latencia observados para estimar bytes por operación. `PipelineGovernor` actualiza su bytes-per-block con cada selección y la telemetría expone tamaño actual/mínimo/máximo. Pendiente únicamente validar en hardware real cómo converge frente a ExtremeCopy.

'@
if (-not $roadmap.Contains($old)) { throw 'Adaptive transfer roadmap block not found.' }
$roadmap = $roadmap.Replace($old, $new)
Set-Content -LiteralPath $roadmapPath -Value $roadmap -Encoding utf8

git diff --check
git status --short
