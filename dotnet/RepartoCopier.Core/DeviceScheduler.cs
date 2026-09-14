using System.Diagnostics;

namespace RepartoCopier.Core;

public sealed record DeviceIoSnapshot(
    string DeviceId,
    int InitialQueueDepth,
    int CurrentQueueDepth,
    int ExplorationQueueDepth,
    int OutstandingIo,
    int PeakOutstandingIo,
    int MinimumObservedQueueDepth,
    int MaximumObservedQueueDepth,
    int BestObservedQueueDepth,
    int QueueDepthUpshifts,
    int QueueDepthDownshifts,
    long BacklogTargetBytes,
    long QueuedBytes,
    long PeakQueuedBytes,
    DeviceIdentityConfidence IdentityConfidence = DeviceIdentityConfidence.Unknown)
{
    public double BacklogPressure => BacklogTargetBytes <= 0
        ? 0
        : Math.Max(0, (double)QueuedBytes / BacklogTargetBytes);
}

/// <summary>
/// Adaptive physical-I/O scheduler shared by every destination on the same
/// physical device. Hardware profiles choose only the starting queue depth.
/// Sustained demand causes multiplicative exploration; measured aggregate
/// throughput and latency decide whether the explored depth is retained or the
/// scheduler returns to the best observed operating point.
/// </summary>
internal sealed class DeviceScheduler : IDisposable
{
    private readonly object _ioGate = new();
    private readonly Queue<IoWaiter> _ioWaiters = new();
    private readonly object _backlogGate = new();
    private readonly int _initialQueueDepth;

    private int _currentQueueDepth;
    private int _minimumObservedQueueDepth;
    private int _maximumObservedQueueDepth;
    private int _bestObservedQueueDepth;
    private int _queueDepthUpshifts;
    private int _queueDepthDownshifts;
    private int _outstandingIo;
    private int _peakOutstandingIo;

    private long _sampleStartedTimestamp;
    private long _sampleBytes;
    private long _sampleLatencyStopwatchTicks;
    private int _sampleCompletions;
    private bool _sampleSawDemand;
    private double _bestThroughputBytesPerSecond;
    private double _bestAverageLatencySeconds = double.PositiveInfinity;

    private long _queuedBytes;
    private long _peakQueuedBytes;
    private bool _disposed;

    internal DeviceScheduler(
        string deviceId,
        int initialQueueDepth,
        long backlogTargetBytes,
        DeviceIdentityConfidence identityConfidence = DeviceIdentityConfidence.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialQueueDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlogTargetBytes);

        DeviceId = deviceId;
        _initialQueueDepth = initialQueueDepth;
        _currentQueueDepth = initialQueueDepth;
        _minimumObservedQueueDepth = initialQueueDepth;
        _maximumObservedQueueDepth = initialQueueDepth;
        _bestObservedQueueDepth = initialQueueDepth;
        BacklogTargetBytes = backlogTargetBytes;
        IdentityConfidence = identityConfidence;
    }

    public string DeviceId { get; }
    public int InitialQueueDepth => _initialQueueDepth;
    public int CurrentQueueDepth => Volatile.Read(ref _currentQueueDepth);
    public int ExplorationQueueDepth => NextExplorationDepth(CurrentQueueDepth);
    public long BacklogTargetBytes { get; }
    public DeviceIdentityConfidence IdentityConfidence { get; }
    public int OutstandingIo => Volatile.Read(ref _outstandingIo);
    public int PeakOutstandingIo => Volatile.Read(ref _peakOutstandingIo);
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);
    public long PeakQueuedBytes => Interlocked.Read(ref _peakQueuedBytes);

    public DeviceIoSnapshot Snapshot()
    {
        lock (_ioGate)
        {
            return new DeviceIoSnapshot(
                DeviceId,
                _initialQueueDepth,
                _currentQueueDepth,
                NextExplorationDepth(_currentQueueDepth),
                _outstandingIo,
                _peakOutstandingIo,
                _minimumObservedQueueDepth,
                _maximumObservedQueueDepth,
                _bestObservedQueueDepth,
                _queueDepthUpshifts,
                _queueDepthDownshifts,
                BacklogTargetBytes,
                QueuedBytes,
                PeakQueuedBytes,
                IdentityConfidence);
        }
    }

    public ValueTask<IoLease> AcquireIoAsync(int bytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        token.ThrowIfCancellationRequested();

        lock (_ioGate)
        {
            ThrowIfDisposed();
            if (_outstandingIo < _currentQueueDepth && _ioWaiters.Count == 0)
                return ValueTask.FromResult(GrantLeaseLocked(bytes));

            _sampleSawDemand = true;
            var waiter = new IoWaiter(bytes);
            _ioWaiters.Enqueue(waiter);
            waiter.Cancellation = token.Register(static state =>
            {
                var pair = ((DeviceScheduler Owner, IoWaiter Waiter, CancellationToken Token))state!;
                pair.Owner.CancelWaiter(pair.Waiter, pair.Token);
            }, (this, waiter, token));
            return new ValueTask<IoLease>(waiter.Completion.Task);
        }
    }

    public bool TryReserveBacklog(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_backlogGate)
        {
            ThrowIfDisposed();
            var queued = Interlocked.Read(ref _queuedBytes);
            if (queued + bytes > BacklogTargetBytes && queued != 0)
                return false;
            ReserveBacklogLocked(bytes);
            return true;
        }
    }

    public ValueTask ReserveBacklogAsync(int bytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        token.ThrowIfCancellationRequested();
        lock (_backlogGate)
        {
            ThrowIfDisposed();
            ReserveBacklogLocked(bytes);
        }
        return ValueTask.CompletedTask;
    }

    public void ReleaseBacklog(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_backlogGate)
        {
            var remaining = Interlocked.Read(ref _queuedBytes) - bytes;
            if (remaining < 0)
                throw new InvalidOperationException("La cola física intentó liberar más bytes de los reservados.");
            Interlocked.Exchange(ref _queuedBytes, remaining);
        }
    }

    private IoLease GrantLeaseLocked(int bytes)
    {
        var outstanding = ++_outstandingIo;
        if (outstanding > _peakOutstandingIo)
            _peakOutstandingIo = outstanding;
        return new IoLease(this, bytes, Stopwatch.GetTimestamp());
    }

    private void CancelWaiter(IoWaiter waiter, CancellationToken token)
    {
        lock (_ioGate)
        {
            if (waiter.Granted || waiter.Cancelled)
                return;
            waiter.Cancelled = true;
            waiter.Completion.TrySetCanceled(token);
        }
    }

    private void ReleaseIo(int bytes, long startedTimestamp)
    {
        List<(IoWaiter Waiter, IoLease Lease)>? ready = null;
        lock (_ioGate)
        {
            var outstanding = --_outstandingIo;
            if (outstanding < 0)
            {
                ++_outstandingIo;
                throw new InvalidOperationException("La contabilidad de I/O físico quedó negativa.");
            }

            RecordCompletionLocked(bytes, startedTimestamp);
            ready = PumpIoWaitersLocked();
        }

        if (ready is null)
            return;
        foreach (var item in ready)
        {
            item.Waiter.Cancellation.Dispose();
            item.Waiter.Completion.TrySetResult(item.Lease);
        }
    }

    private List<(IoWaiter Waiter, IoLease Lease)>? PumpIoWaitersLocked()
    {
        List<(IoWaiter, IoLease)>? ready = null;
        while (_outstandingIo < _currentQueueDepth && _ioWaiters.Count > 0)
        {
            var waiter = _ioWaiters.Dequeue();
            if (waiter.Cancelled)
            {
                waiter.Cancellation.Dispose();
                continue;
            }

            waiter.Granted = true;
            (ready ??= []).Add((waiter, GrantLeaseLocked(waiter.Bytes)));
        }
        return ready;
    }

    private void RecordCompletionLocked(int bytes, long startedTimestamp)
    {
        var now = Stopwatch.GetTimestamp();
        if (_sampleStartedTimestamp == 0 || startedTimestamp < _sampleStartedTimestamp)
            _sampleStartedTimestamp = startedTimestamp;
        _sampleBytes = checked(_sampleBytes + bytes);
        _sampleLatencyStopwatchTicks = checked(_sampleLatencyStopwatchTicks + Math.Max(1, now - startedTimestamp));
        _sampleCompletions++;
        if (_ioWaiters.Count > 0 || _outstandingIo >= _currentQueueDepth)
            _sampleSawDemand = true;

        var decisionInterval = Math.Max(8, Math.Min(256, _currentQueueDepth * 2));
        if (_sampleCompletions < decisionInterval)
            return;

        var elapsedTicks = Math.Max(1, now - _sampleStartedTimestamp);
        var throughput = _sampleBytes * (double)Stopwatch.Frequency / elapsedTicks;
        var averageLatency = _sampleLatencyStopwatchTicks /
            ((double)Stopwatch.Frequency * Math.Max(1, _sampleCompletions));
        var sawDemand = _sampleSawDemand;
        ResetSampleLocked();

        if (!sawDemand)
            return;

        if (_bestThroughputBytesPerSecond <= 0)
        {
            ObserveBestLocked(throughput, averageLatency);
            UpshiftLocked();
            return;
        }

        if (_currentQueueDepth > _bestObservedQueueDepth)
        {
            var throughputRatio = throughput / _bestThroughputBytesPerSecond;
            var latencyRatio = averageLatency / Math.Max(double.Epsilon, _bestAverageLatencySeconds);

            if (throughputRatio >= 1.01 || (throughputRatio >= 0.995 && latencyRatio <= 1.10))
            {
                ObserveBestLocked(throughput, averageLatency);
                UpshiftLocked();
                return;
            }

            if (throughputRatio < 0.97 || latencyRatio > 1.35)
            {
                DownshiftToBestLocked();
                return;
            }

            // Ambiguous but competitive result: keep exploring rather than
            // freezing at a conservative hardware-class number.
            ObserveBestLocked(Math.Max(throughput, _bestThroughputBytesPerSecond),
                Math.Min(averageLatency, _bestAverageLatencySeconds));
            UpshiftLocked();
            return;
        }

        if (throughput >= _bestThroughputBytesPerSecond * 0.995)
            ObserveBestLocked(Math.Max(throughput, _bestThroughputBytesPerSecond),
                Math.Min(averageLatency, _bestAverageLatencySeconds));
        UpshiftLocked();
    }

    private void ObserveBestLocked(double throughput, double averageLatency)
    {
        _bestObservedQueueDepth = _currentQueueDepth;
        _bestThroughputBytesPerSecond = throughput;
        _bestAverageLatencySeconds = averageLatency;
    }

    private void UpshiftLocked()
    {
        var next = NextExplorationDepth(_currentQueueDepth);
        if (next <= _currentQueueDepth)
            return;
        _currentQueueDepth = next;
        _queueDepthUpshifts++;
        if (next > _maximumObservedQueueDepth)
            _maximumObservedQueueDepth = next;
    }

    private void DownshiftToBestLocked()
    {
        var next = Math.Max(1, _bestObservedQueueDepth);
        if (next >= _currentQueueDepth)
            return;
        _currentQueueDepth = next;
        _queueDepthDownshifts++;
        if (next < _minimumObservedQueueDepth)
            _minimumObservedQueueDepth = next;
    }

    private void ResetSampleLocked()
    {
        _sampleStartedTimestamp = 0;
        _sampleBytes = 0;
        _sampleLatencyStopwatchTicks = 0;
        _sampleCompletions = 0;
        _sampleSawDemand = false;
    }

    private static int NextExplorationDepth(int current) =>
        current >= int.MaxValue / 2 ? int.MaxValue : Math.Max(current + 1, current * 2);

    private void ReserveBacklogLocked(int bytes)
    {
        var queued = Interlocked.Add(ref _queuedBytes, bytes);
        UpdateMax(ref _peakQueuedBytes, queued);
    }

    public void Dispose()
    {
        List<IoWaiter>? cancelled = null;
        lock (_ioGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            while (_ioWaiters.Count > 0)
                (cancelled ??= []).Add(_ioWaiters.Dequeue());
        }

        if (cancelled is not null)
        {
            foreach (var waiter in cancelled)
            {
                waiter.Cancelled = true;
                waiter.Cancellation.Dispose();
                waiter.Completion.TrySetException(new ObjectDisposedException(nameof(DeviceScheduler)));
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void UpdateMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private sealed class IoWaiter(int bytes)
    {
        public int Bytes { get; } = bytes;
        public TaskCompletionSource<IoLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Cancellation { get; set; }
        public bool Granted { get; set; }
        public bool Cancelled { get; set; }
    }

    internal sealed class IoLease : IDisposable
    {
        private DeviceScheduler? _owner;
        private readonly int _bytes;
        private readonly long _startedTimestamp;

        internal IoLease(DeviceScheduler owner, int bytes, long startedTimestamp)
        {
            _owner = owner;
            _bytes = bytes;
            _startedTimestamp = startedTimestamp;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseIo(_bytes, _startedTimestamp);
        }
    }
}

internal sealed class DeviceSchedulerMap : IDisposable
{
    private readonly Dictionary<string, DeviceScheduler> _schedulers;

    private DeviceSchedulerMap(Dictionary<string, DeviceScheduler> schedulers) =>
        _schedulers = schedulers;

    public IReadOnlyCollection<DeviceScheduler> Schedulers => _schedulers.Values;

    public IReadOnlyList<DeviceIoSnapshot> Snapshot() =>
        _schedulers.Values
            .Select(item => item.Snapshot())
            .OrderBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public DeviceScheduler For(StorageDeviceInfo device) =>
        _schedulers.TryGetValue(device.PhysicalDeviceId, out var scheduler)
            ? scheduler
            : throw new KeyNotFoundException($"No existe scheduler para {device.PhysicalDeviceId}.");

    public static DeviceSchedulerMap Create(
        StorageDeviceInfo? source,
        IEnumerable<StorageDeviceInfo> destinations)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        var destinationArray = destinations.ToArray();
        var groups = destinationArray.GroupBy(item => item.PhysicalDeviceId, StringComparer.OrdinalIgnoreCase);
        var schedulers = new Dictionary<string, DeviceScheduler>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var materializedGroup = group.ToArray();
            var profiles = materializedGroup.Select(StorageIoProfile.For).ToArray();
            var initialDepth = profiles.Min(profile => profile.InitialQueueDepth);
            var backlogTarget = profiles.Min(profile => profile.DeviceBacklogTargetBytes);
            var confidence = (DeviceIdentityConfidence)materializedGroup
                .Min(item => (int)StorageDeviceIdentity.ConfidenceFor(item));

            // Sharing source/destination on one physical disk starts cautiously
            // to avoid immediate seek thrash, but it is not a permanent QD1 cap.
            if (source is not null && SharesPhysicalDevice(source, materializedGroup[0]))
                initialDepth = 1;

            schedulers.Add(
                group.Key,
                new DeviceScheduler(group.Key, initialDepth, backlogTarget, confidence));
        }

        return new DeviceSchedulerMap(schedulers);
    }

    internal static bool SharesPhysicalDevice(StorageDeviceInfo left, StorageDeviceInfo right) =>
        left.PhysicalDeviceNumber is uint leftNumber &&
        right.PhysicalDeviceNumber is uint rightNumber &&
        leftNumber == rightNumber;

    public void Dispose()
    {
        foreach (var scheduler in _schedulers.Values)
            scheduler.Dispose();
        _schedulers.Clear();
    }
}
