namespace RepartoCopier.Core;

public sealed record DeviceIoSnapshot(
    string DeviceId,
    int MaxOutstandingIo,
    int OutstandingIo,
    int PeakOutstandingIo,
    long BacklogTargetBytes,
    long QueuedBytes,
    long PeakQueuedBytes)
{
    public double BacklogPressure => BacklogTargetBytes <= 0
        ? 0
        : Math.Max(0, (double)QueuedBytes / BacklogTargetBytes);
}

/// <summary>
/// Coordinates physical-I/O pressure for destinations that resolve to the same
/// device. It deliberately does not synchronize file completion between workers.
/// </summary>
internal sealed class DeviceScheduler : IDisposable
{
    private readonly SemaphoreSlim _ioSlots;
    private readonly object _backlogGate = new();
    private readonly Queue<BacklogWaiter> _backlogWaiters = new();
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

    public DeviceIoSnapshot Snapshot() => new(
        DeviceId,
        MaxOutstandingIo,
        OutstandingIo,
        PeakOutstandingIo,
        BacklogTargetBytes,
        QueuedBytes,
        PeakQueuedBytes);

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
    /// Tries to reserve physical-device backlog immediately. FAN-OUT uses this
    /// first so uncongested devices receive the SharedBlock before the producer
    /// waits on a slower device. One oversized block is allowed when the queue is
    /// empty so conservative targets can never deadlock forward progress.
    /// </summary>
    public bool TryReserveBacklog(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_backlogGate)
        {
            ThrowIfDisposed();
            if (_backlogWaiters.Count != 0 || !CanReserveBacklogLocked(bytes))
                return false;
            ReserveBacklogLocked(bytes);
            return true;
        }
    }

    public ValueTask ReserveBacklogAsync(int bytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_backlogGate)
        {
            ThrowIfDisposed();
            if (_backlogWaiters.Count == 0 && CanReserveBacklogLocked(bytes))
            {
                ReserveBacklogLocked(bytes);
                return ValueTask.CompletedTask;
            }

            var waiter = new BacklogWaiter(bytes);
            _backlogWaiters.Enqueue(waiter);
            return new ValueTask(WaitForBacklogAsync(waiter, token));
        }
    }

    public void ReleaseBacklog(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        List<BacklogWaiter>? ready;
        lock (_backlogGate)
        {
            var remaining = Interlocked.Read(ref _queuedBytes) - bytes;
            if (remaining < 0)
                throw new InvalidOperationException("La cola física intentó liberar más bytes de los reservados.");
            Interlocked.Exchange(ref _queuedBytes, remaining);
            ready = PumpBacklogWaitersLocked();
        }
        CompleteBacklogWaiters(ready);
    }

    // Kept for focused scheduler tests/diagnostics. Production FAN-OUT uses the
    // reservation APIs above so the target is actually enforced.
    public void NoteQueuedBytes(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        var queued = Interlocked.Add(ref _queuedBytes, bytes);
        UpdateMax(ref _peakQueuedBytes, queued);
    }

    public void NoteDequeuedBytes(int bytes) => ReleaseBacklog(bytes);

    private async Task WaitForBacklogAsync(BacklogWaiter waiter, CancellationToken token)
    {
        try
        {
            await waiter.Completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            List<BacklogWaiter>? ready;
            lock (_backlogGate)
            {
                if (waiter.Granted)
                {
                    waiter.Granted = false;
                    var remaining = Interlocked.Read(ref _queuedBytes) - waiter.Bytes;
                    if (remaining < 0)
                        throw new InvalidOperationException("Contabilidad de backlog inválida durante cancelación.");
                    Interlocked.Exchange(ref _queuedBytes, remaining);
                }
                else
                {
                    waiter.Cancelled = true;
                }
                ready = PumpBacklogWaitersLocked();
            }
            CompleteBacklogWaiters(ready);
            throw;
        }
    }

    private bool CanReserveBacklogLocked(int bytes)
    {
        var queued = Interlocked.Read(ref _queuedBytes);
        return queued + bytes <= BacklogTargetBytes || queued == 0;
    }

    private void ReserveBacklogLocked(int bytes)
    {
        var queued = Interlocked.Add(ref _queuedBytes, bytes);
        UpdateMax(ref _peakQueuedBytes, queued);
    }

    private List<BacklogWaiter>? PumpBacklogWaitersLocked()
    {
        List<BacklogWaiter>? ready = null;
        while (_backlogWaiters.Count > 0)
        {
            var waiter = _backlogWaiters.Peek();
            if (waiter.Cancelled)
            {
                _backlogWaiters.Dequeue();
                continue;
            }
            if (!CanReserveBacklogLocked(waiter.Bytes))
                break;
            _backlogWaiters.Dequeue();
            ReserveBacklogLocked(waiter.Bytes);
            waiter.Granted = true;
            (ready ??= []).Add(waiter);
        }
        return ready;
    }

    private static void CompleteBacklogWaiters(List<BacklogWaiter>? ready)
    {
        if (ready is null)
            return;
        foreach (var waiter in ready)
            waiter.Completion.TrySetResult();
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
        List<BacklogWaiter> pending = [];
        lock (_backlogGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            while (_backlogWaiters.Count > 0)
                pending.Add(_backlogWaiters.Dequeue());
        }
        foreach (var waiter in pending)
            waiter.Completion.TrySetException(new ObjectDisposedException(nameof(DeviceScheduler)));
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

    private sealed class BacklogWaiter(int bytes)
    {
        public int Bytes { get; } = bytes;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; set; }
        public bool Granted { get; set; }
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
