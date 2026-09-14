namespace RepartoCopier.Core;

public sealed record DeviceIoSnapshot(
    string DeviceId,
    int MaxOutstandingIo,
    int OutstandingIo,
    int PeakOutstandingIo,
    long BacklogTargetBytes,
    long QueuedBytes,
    long PeakQueuedBytes,
    DeviceIdentityConfidence IdentityConfidence = DeviceIdentityConfidence.Unknown)
{
    /// <summary>
    /// Soft FAN-OUT backlog pressure. Values above 1 are intentionally allowed:
    /// branch backlog is not a producer gate; the global FAN-OUT memory budget is.
    /// </summary>
    public double BacklogPressure => BacklogTargetBytes <= 0
        ? 0
        : Math.Max(0, (double)QueuedBytes / BacklogTargetBytes);
}

/// <summary>
/// Coordinates physical-I/O pressure for destinations that resolve to the same
/// device. Every physical operation acquires exactly one slot immediately before
/// I/O. Variable queue depth is created by multiple independent operations rather
/// than reserving fixed pairs/batches ahead of time.
/// </summary>
internal sealed class DeviceScheduler : IDisposable
{
    private readonly SemaphoreSlim _ioSlots;
    private readonly object _backlogGate = new();
    private int _outstandingIo;
    private int _peakOutstandingIo;
    private long _queuedBytes;
    private long _peakQueuedBytes;
    private bool _disposed;

    internal DeviceScheduler(
        string deviceId,
        int maxOutstandingIo,
        long backlogTargetBytes,
        DeviceIdentityConfidence identityConfidence = DeviceIdentityConfidence.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutstandingIo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlogTargetBytes);

        DeviceId = deviceId;
        MaxOutstandingIo = maxOutstandingIo;
        BacklogTargetBytes = backlogTargetBytes;
        IdentityConfidence = identityConfidence;
        _ioSlots = new SemaphoreSlim(maxOutstandingIo, maxOutstandingIo);
    }

    public string DeviceId { get; }
    public int MaxOutstandingIo { get; }
    public long BacklogTargetBytes { get; }
    public DeviceIdentityConfidence IdentityConfidence { get; }
    public int OutstandingIo => Volatile.Read(ref _outstandingIo);
    public int PeakOutstandingIo => Volatile.Read(ref _peakOutstandingIo);
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);
    public long PeakQueuedBytes => Interlocked.Read(ref _peakQueuedBytes);

    public DeviceIoSnapshot Snapshot() => new(
        DeviceId,
        MaxOutstandingIo,
        OutstandingIo,
        PeakOutstandingIo,
        BacklogTargetBytes,
        QueuedBytes,
        PeakQueuedBytes,
        IdentityConfidence);

    public async ValueTask<IoLease> AcquireIoAsync(CancellationToken token)
    {
        ThrowIfDisposed();
        try
        {
            await _ioSlots.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException(token);
        }

        if (_disposed)
        {
            _ioSlots.Release();
            throw new ObjectDisposedException(nameof(DeviceScheduler));
        }

        var outstanding = Interlocked.Increment(ref _outstandingIo);
        UpdateMax(ref _peakOutstandingIo, outstanding);
        return new IoLease(this);
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

    private void ReserveBacklogLocked(int bytes)
    {
        var queued = Interlocked.Add(ref _queuedBytes, bytes);
        UpdateMax(ref _peakQueuedBytes, queued);
    }

    private void ReleaseIo()
    {
        var outstanding = Interlocked.Decrement(ref _outstandingIo);
        if (outstanding < 0)
        {
            Interlocked.Increment(ref _outstandingIo);
            throw new InvalidOperationException("La contabilidad de I/O físico quedó negativa.");
        }
        _ioSlots.Release();
    }

    public void Dispose()
    {
        lock (_backlogGate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _ioSlots.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void UpdateMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

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

    internal sealed class IoLease : IDisposable
    {
        private DeviceScheduler? _owner;

        internal IoLease(DeviceScheduler owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseIo();
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
            var maxOutstanding = profiles.Min(profile => profile.RecommendedQueueDepth);
            var backlogTarget = profiles.Min(profile => profile.DeviceBacklogTargetBytes);
            var confidence = (DeviceIdentityConfidence)materializedGroup
                .Min(item => (int)StorageDeviceIdentity.ConfidenceFor(item));

            if (source is not null && SharesPhysicalDevice(source, materializedGroup[0]))
                maxOutstanding = 1;

            schedulers.Add(
                group.Key,
                new DeviceScheduler(group.Key, maxOutstanding, backlogTarget, confidence));
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
