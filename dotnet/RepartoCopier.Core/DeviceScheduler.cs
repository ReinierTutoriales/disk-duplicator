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
/// device. Physical queue depth is a hard device limit. BacklogTargetBytes is a
/// soft per-device watermark used to classify queue pressure; it must never make
/// one slow destination stall the FAN-OUT producer while global shared-buffer
/// memory remains available.
/// </summary>
internal sealed class DeviceScheduler : IDisposable
{
    private readonly object _ioGate = new();
    private readonly Queue<IoWaiter> _ioWaiters = new();
    private readonly object _backlogGate = new();
    private int _availableIo;
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
        _availableIo = maxOutstandingIo;
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
        await AcquireIoSlotsAsync(1, token).ConfigureAwait(false);
        return new IoLease(this);
    }

    public async ValueTask<IoPairLease> AcquireIoPairAsync(CancellationToken token)
    {
        if (MaxOutstandingIo < 2)
            throw new InvalidOperationException("El scheduler no permite adquirir dos operaciones de I/O simultáneas.");

        await AcquireIoSlotsAsync(2, token).ConfigureAwait(false);
        return new IoPairLease(this);
    }

    private ValueTask AcquireIoSlotsAsync(int slots, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);
        if (slots > MaxOutstandingIo)
            throw new InvalidOperationException($"El scheduler no permite adquirir {slots} operaciones de I/O simultáneas.");
        token.ThrowIfCancellationRequested();

        lock (_ioGate)
        {
            ThrowIfDisposed();
            if (_ioWaiters.Count == 0 && _availableIo >= slots)
            {
                GrantIoSlotsLocked(slots);
                return ValueTask.CompletedTask;
            }

            var waiter = new IoWaiter(slots);
            _ioWaiters.Enqueue(waiter);
            return new ValueTask(WaitForIoAsync(waiter, token));
        }
    }

    private async Task WaitForIoAsync(IoWaiter waiter, CancellationToken token)
    {
        try
        {
            await waiter.Completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            List<IoWaiter>? ready;
            lock (_ioGate)
            {
                if (waiter.Granted)
                {
                    waiter.Granted = false;
                    ReleaseIoSlotsLocked(waiter.Slots);
                }
                else
                {
                    waiter.Cancelled = true;
                }
                ready = PumpIoWaitersLocked();
            }
            CompleteIoWaiters(ready);
            throw;
        }
    }

    private void GrantIoSlotsLocked(int slots)
    {
        if (_availableIo < slots)
            throw new InvalidOperationException("El scheduler intentó conceder más I/O del disponible.");
        _availableIo -= slots;
        _outstandingIo += slots;
        UpdateMax(ref _peakOutstandingIo, _outstandingIo);
    }

    private void ReleaseIo(int slots)
    {
        List<IoWaiter>? ready;
        lock (_ioGate)
        {
            ReleaseIoSlotsLocked(slots);
            ready = PumpIoWaitersLocked();
        }
        CompleteIoWaiters(ready);
    }

    private void ReleaseIoSlotsLocked(int slots)
    {
        if (slots <= 0 || _outstandingIo < slots || _availableIo + slots > MaxOutstandingIo)
            throw new InvalidOperationException("La contabilidad de I/O físico quedó inválida.");
        _outstandingIo -= slots;
        _availableIo += slots;
    }

    private List<IoWaiter>? PumpIoWaitersLocked()
    {
        List<IoWaiter>? ready = null;
        while (_ioWaiters.Count > 0)
        {
            var waiter = _ioWaiters.Peek();
            if (waiter.Cancelled)
            {
                _ioWaiters.Dequeue();
                continue;
            }
            if (_availableIo < waiter.Slots)
                break;

            _ioWaiters.Dequeue();
            GrantIoSlotsLocked(waiter.Slots);
            waiter.Granted = true;
            (ready ??= []).Add(waiter);
        }
        return ready;
    }

    private static void CompleteIoWaiters(List<IoWaiter>? ready)
    {
        if (ready is null)
            return;
        foreach (var waiter in ready)
            waiter.Completion.TrySetResult();
    }

    /// <summary>
    /// Reserves backlog immediately while the branch remains at or below its soft
    /// target. FAN-OUT calls this first so branches under normal pressure are
    /// admitted before overflowed branches.
    /// </summary>
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

    /// <summary>
    /// Admits a branch above its soft backlog target without waiting for that
    /// branch to drain. This is intentional: a per-device queue watermark must
    /// not become global FAN-OUT backpressure. AdaptiveByteBudget remains the hard
    /// shared-payload memory ceiling and physical I/O is still bounded by
    /// AcquireIoAsync/AcquireIoPairAsync.
    /// </summary>
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

    public void Dispose()
    {
        List<IoWaiter>? pending = null;
        lock (_ioGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            while (_ioWaiters.Count > 0)
            {
                var waiter = _ioWaiters.Dequeue();
                if (!waiter.Cancelled && !waiter.Granted)
                    (pending ??= []).Add(waiter);
            }
        }

        if (pending is not null)
        {
            foreach (var waiter in pending)
                waiter.Completion.TrySetException(new ObjectDisposedException(nameof(DeviceScheduler)));
        }
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

    private sealed class IoWaiter(int slots)
    {
        public int Slots { get; } = slots;
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
            owner?.ReleaseIo(1);
        }
    }

    internal sealed class IoPairLease : IDisposable
    {
        private DeviceScheduler? _owner;

        internal IoPairLease(DeviceScheduler owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseIo(2);
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
