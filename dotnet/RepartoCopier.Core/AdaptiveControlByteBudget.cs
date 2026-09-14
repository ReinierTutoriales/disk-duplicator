namespace RepartoCopier.Core;

/// <summary>
/// Accounts only queued control-plane delivery memory. Capacity follows the same
/// runtime/OS high-memory-load signal used by FAN-OUT payload memory; there is no
/// fixed message-count ceiling. Under available headroom admission stays on the
/// synchronous fast path. Backpressure appears only when real memory pressure
/// leaves insufficient byte capacity for another queued control delivery.
/// </summary>
internal sealed class AdaptiveControlByteBudget
{
    // One queued delivery owns a channel node/reference plus this small ownership
    // envelope. This is a memory-accounting estimate, not a message-count cap.
    // It scales with the runtime pointer width and is converted to bytes before
    // admission so all pressure decisions remain in one unit: memory bytes.
    internal static int EstimatedDeliveryBytes => checked(IntPtr.Size * 8);

    private readonly object _gate = new();
    private readonly Queue<Waiter> _waiters = new();
    private readonly Func<long, long> _capacityProvider;
    private long _usedBytes;
    private long _peakBytes;

    internal AdaptiveControlByteBudget(Func<long, long> capacityProvider)
    {
        _capacityProvider = capacityProvider ?? throw new ArgumentNullException(nameof(capacityProvider));
    }

    internal long UsedBytes
    {
        get { lock (_gate) return _usedBytes; }
    }

    internal long PeakBytes
    {
        get { lock (_gate) return _peakBytes; }
    }

    internal static AdaptiveControlByteBudget CreateForSystem() =>
        new(usedBytes => MemoryPressureCapacity.GetSafeTotalBytes(
            usedBytes,
            EstimatedDeliveryBytes));

    internal ValueTask AcquireAsync(int bytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_gate)
        {
            if (_waiters.Count == 0 && TryAcquireLocked(bytes))
                return ValueTask.CompletedTask;

            var waiter = new Waiter(bytes);
            _waiters.Enqueue(waiter);
            return new ValueTask(WaitAsync(waiter, token));
        }
    }

    internal void Release(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        List<Waiter>? ready;
        lock (_gate)
        {
            if (_usedBytes < bytes)
                throw new InvalidOperationException("Se intentó liberar más memoria de control de la reservada.");
            _usedBytes -= bytes;
            ready = PumpWaitersLocked();
        }
        Complete(ready);
    }

    private async Task WaitAsync(Waiter waiter, CancellationToken token)
    {
        try
        {
            await waiter.Completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            List<Waiter>? ready;
            lock (_gate)
            {
                if (waiter.Granted)
                {
                    waiter.Granted = false;
                    if (_usedBytes < waiter.Bytes)
                        throw new InvalidOperationException("Contabilidad de memoria de control inválida durante cancelación.");
                    _usedBytes -= waiter.Bytes;
                }
                else
                {
                    waiter.Cancelled = true;
                }
                ready = PumpWaitersLocked();
            }
            Complete(ready);
            throw;
        }
    }

    private bool TryAcquireLocked(int bytes)
    {
        var capacity = Math.Max(_usedBytes, _capacityProvider(_usedBytes));
        if (_usedBytes > long.MaxValue - bytes || _usedBytes + bytes > capacity)
            return false;

        _usedBytes += bytes;
        _peakBytes = Math.Max(_peakBytes, _usedBytes);
        return true;
    }

    private List<Waiter>? PumpWaitersLocked()
    {
        List<Waiter>? ready = null;
        while (_waiters.Count > 0)
        {
            var waiter = _waiters.Peek();
            if (waiter.Cancelled)
            {
                _waiters.Dequeue();
                continue;
            }
            if (!TryAcquireLocked(waiter.Bytes))
                break;
            _waiters.Dequeue();
            waiter.Granted = true;
            (ready ??= []).Add(waiter);
        }
        return ready;
    }

    private static void Complete(List<Waiter>? ready)
    {
        if (ready is null)
            return;
        foreach (var waiter in ready)
            waiter.Completion.TrySetResult();
    }

    private sealed class Waiter(int bytes)
    {
        internal int Bytes { get; } = bytes;
        internal TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cancelled { get; set; }
        internal bool Granted { get; set; }
    }
}
