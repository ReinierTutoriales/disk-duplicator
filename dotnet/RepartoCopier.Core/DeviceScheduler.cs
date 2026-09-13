namespace RepartoCopier.Core;

/// <summary>
/// Coordinates physical-I/O pressure for destinations that resolve to the same
/// device. It deliberately does not synchronize file completion between workers.
/// </summary>
internal sealed class DeviceScheduler : IDisposable
{
    private readonly SemaphoreSlim _ioSlots;
    private int _outstandingIo;
    private int _peakOutstandingIo;
    private long _queuedBytes;
    private long _peakQueuedBytes;
    private bool _disposed;

    internal DeviceScheduler(string deviceId, int maxOutstandingIo, long backlogTargetBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutstandingIo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlogTargetBytes);

        DeviceId = deviceId;
        MaxOutstandingIo = maxOutstandingIo;
        BacklogTargetBytes = backlogTargetBytes;
        _ioSlots = new SemaphoreSlim(maxOutstandingIo, maxOutstandingIo);
    }

    public string DeviceId { get; }
    public int MaxOutstandingIo { get; }
    public long BacklogTargetBytes { get; }
    public int OutstandingIo => Volatile.Read(ref _outstandingIo);
    public int PeakOutstandingIo => Volatile.Read(ref _peakOutstandingIo);
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);
    public long PeakQueuedBytes => Interlocked.Read(ref _peakQueuedBytes);

    public async ValueTask<IoLease> AcquireIoAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _ioSlots.WaitAsync(token).ConfigureAwait(false);
        if (_disposed)
        {
            _ioSlots.Release();
            throw new ObjectDisposedException(nameof(DeviceScheduler));
        }

        var outstanding = Interlocked.Increment(ref _outstandingIo);
        UpdateMax(ref _peakOutstandingIo, outstanding);
        return new IoLease(this);
    }

    /// <summary>
    /// Observes bytes queued for this physical device. This is telemetry-only in
    /// the first scheduler stage; a later backlog budget can enforce the target
    /// without changing FAN-OUT file ordering.
    /// </summary>
    public void NoteQueuedBytes(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        var queued = Interlocked.Add(ref _queuedBytes, bytes);
        UpdateMax(ref _peakQueuedBytes, queued);
    }

    public void NoteDequeuedBytes(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        var remaining = Interlocked.Add(ref _queuedBytes, -bytes);
        if (remaining < 0)
        {
            Interlocked.Add(ref _queuedBytes, bytes);
            throw new InvalidOperationException("La cola física intentó liberar más bytes de los registrados.");
        }
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
        if (_disposed)
            return;
        _disposed = true;
        _ioSlots.Dispose();
    }

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
            var profiles = group.Select(StorageIoProfile.For).ToArray();
            var maxOutstanding = profiles.Min(profile => profile.RecommendedQueueDepth);
            var backlogTarget = profiles.Min(profile => profile.DeviceBacklogTargetBytes);

            if (source is not null && SharesPhysicalDevice(source, group.First()))
                maxOutstanding = 1;

            schedulers.Add(group.Key, new DeviceScheduler(group.Key, maxOutstanding, backlogTarget));
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
