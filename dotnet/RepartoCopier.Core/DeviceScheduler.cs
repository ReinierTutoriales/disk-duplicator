using System.Threading;

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
    string LastQueueDepthDecision,
    double BestObservedThroughputBytesPerSecond,
    double BestObservedAverageLatencyMilliseconds,
    long BacklogTargetBytes,
    long QueuedBytes,
    long PeakQueuedBytes,
    DeviceIdentityConfidence IdentityConfidence = DeviceIdentityConfidence.Unknown)
{
    public double BacklogPressure => BacklogTargetBytes <= 0 ? 0 : Math.Max(0, (double)QueuedBytes / BacklogTargetBytes);
}

/// <summary>
/// Fixed one-I/O gate per physical device. There is deliberately no queue-depth
/// exploration, throughput probing, multiplicative growth, or latency tuning.
/// This mirrors ExtremeCopy's stable synchronous destination model while the
/// shared 256 MiB FAN-OUT pool provides the buffering/backpressure window.
/// </summary>
internal sealed class DeviceScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<IoWaiter> _waiters = new();
    private readonly object _backlogGate = new();
    private int _outstandingIo;
    private int _peakOutstandingIo;
    private long _queuedBytes;
    private long _peakQueuedBytes;
    private bool _disposed;

    internal DeviceScheduler(string deviceId, int initialQueueDepth, long backlogTargetBytes, DeviceIdentityConfidence identityConfidence = DeviceIdentityConfidence.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialQueueDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlogTargetBytes);
        DeviceId = deviceId;
        BacklogTargetBytes = backlogTargetBytes;
        IdentityConfidence = identityConfidence;
    }

    public string DeviceId { get; }
    public int InitialQueueDepth => 1;
    public int CurrentQueueDepth => 1;
    public int ExplorationQueueDepth => 1;
    public long BacklogTargetBytes { get; }
    public DeviceIdentityConfidence IdentityConfidence { get; }
    public int OutstandingIo => Volatile.Read(ref _outstandingIo);
    public int PeakOutstandingIo => Volatile.Read(ref _peakOutstandingIo);
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);
    public long PeakQueuedBytes => Interlocked.Read(ref _peakQueuedBytes);

    public DeviceIoSnapshot Snapshot() => new(
        DeviceId, 1, 1, 1, OutstandingIo, PeakOutstandingIo,
        1, 1, 1, 0, 0, "fixed:extreme-style", 0, 0,
        BacklogTargetBytes, QueuedBytes, PeakQueuedBytes, IdentityConfidence);

    public ValueTask<IoLease> AcquireIoAsync(int bytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_outstandingIo == 0 && _waiters.Count == 0)
                return ValueTask.FromResult(GrantLeaseLocked(bytes));
            var waiter = new IoWaiter(bytes);
            _waiters.Enqueue(waiter);
            waiter.Cancellation = token.Register(static state =>
            {
                var pair = ((DeviceScheduler Owner, IoWaiter Waiter, CancellationToken Token))state!;
                pair.Owner.CancelWaiter(pair.Waiter, pair.Token);
            }, (this, waiter, token));
            return new ValueTask<IoLease>(waiter.Completion.Task);
        }
    }

    public void ReserveBacklog(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_backlogGate)
        {
            ThrowIfDisposed();
            var queued = Interlocked.Add(ref _queuedBytes, bytes);
            UpdateMax(ref _peakQueuedBytes, queued);
        }
    }

    public void ReleaseBacklog(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_backlogGate)
        {
            var remaining = Interlocked.Read(ref _queuedBytes) - bytes;
            if (remaining < 0) throw new InvalidOperationException("La cola física intentó liberar más bytes de los reservados.");
            Interlocked.Exchange(ref _queuedBytes, remaining);
        }
    }

    private IoLease GrantLeaseLocked(int bytes)
    {
        _outstandingIo = 1;
        if (_peakOutstandingIo < 1) _peakOutstandingIo = 1;
        return new IoLease(this, bytes);
    }

    private void CancelWaiter(IoWaiter waiter, CancellationToken token)
    {
        lock (_gate)
        {
            if (waiter.Granted || waiter.Cancelled) return;
            waiter.Cancelled = true;
            waiter.Completion.TrySetCanceled(token);
        }
    }

    private void ReleaseIo()
    {
        IoWaiter? ready = null;
        IoLease? lease = null;
        lock (_gate)
        {
            if (_outstandingIo != 1) throw new InvalidOperationException("La contabilidad de I/O físico quedó inválida.");
            _outstandingIo = 0;
            while (_waiters.Count > 0)
            {
                var candidate = _waiters.Dequeue();
                if (candidate.Cancelled)
                {
                    candidate.Cancellation.Dispose();
                    continue;
                }
                candidate.Granted = true;
                ready = candidate;
                lease = GrantLeaseLocked(candidate.Bytes);
                break;
            }
        }
        if (ready is not null)
        {
            ready.Cancellation.Dispose();
            ready.Completion.TrySetResult(lease!);
        }
    }

    public void Dispose()
    {
        List<IoWaiter>? cancelled = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            while (_waiters.Count > 0) (cancelled ??= []).Add(_waiters.Dequeue());
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
            if (observed == current) return;
            current = observed;
        }
    }

    private sealed class IoWaiter(int bytes)
    {
        public int Bytes { get; } = bytes;
        public TaskCompletionSource<IoLease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Cancellation { get; set; }
        public bool Granted { get; set; }
        public bool Cancelled { get; set; }
    }

    internal sealed class IoLease : IDisposable
    {
        private DeviceScheduler? _owner;
        internal IoLease(DeviceScheduler owner, int bytes) { _owner = owner; }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseIo();
    }
}

internal sealed class DeviceSchedulerMap : IDisposable
{
    private readonly Dictionary<string, DeviceScheduler> _schedulers;
    private DeviceSchedulerMap(Dictionary<string, DeviceScheduler> schedulers, DeviceScheduler? sharedSourceScheduler)
    {
        _schedulers = schedulers;
        SharedSourceScheduler = sharedSourceScheduler;
    }

    public IReadOnlyCollection<DeviceScheduler> Schedulers => _schedulers.Values;
    public DeviceScheduler? SharedSourceScheduler { get; }
    public IReadOnlyList<DeviceIoSnapshot> Snapshot() => _schedulers.Values.Select(item => item.Snapshot()).OrderBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase).ToArray();
    public DeviceScheduler For(StorageDeviceInfo device) => _schedulers.TryGetValue(device.PhysicalDeviceId, out var scheduler) ? scheduler : throw new KeyNotFoundException($"No existe scheduler para {device.PhysicalDeviceId}.");

    public static DeviceSchedulerMap Create(StorageDeviceInfo? source, IEnumerable<StorageDeviceInfo> destinations)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        var groups = destinations.ToArray().GroupBy(item => item.PhysicalDeviceId, StringComparer.OrdinalIgnoreCase);
        var schedulers = new Dictionary<string, DeviceScheduler>(StringComparer.OrdinalIgnoreCase);
        DeviceScheduler? sharedSourceScheduler = null;
        foreach (var group in groups)
        {
            var devices = group.ToArray();
            var profiles = devices.Select(StorageIoProfile.For).ToArray();
            var backlogTarget = profiles.Min(profile => profile.DeviceBacklogTargetBytes);
            var confidence = (DeviceIdentityConfidence)devices.Min(item => (int)StorageDeviceIdentity.ConfidenceFor(item));
            var scheduler = new DeviceScheduler(group.Key, 1, backlogTarget, confidence);
            schedulers.Add(group.Key, scheduler);
            if (source is not null && SharesPhysicalDevice(source, devices[0])) sharedSourceScheduler = scheduler;
        }
        return new DeviceSchedulerMap(schedulers, sharedSourceScheduler);
    }

    internal static bool SharesPhysicalDevice(StorageDeviceInfo left, StorageDeviceInfo right) => StorageDeviceIdentity.SamePhysicalDevice(left, right);

    public void Dispose()
    {
        foreach (var scheduler in _schedulers.Values) scheduler.Dispose();
        _schedulers.Clear();
    }
}
