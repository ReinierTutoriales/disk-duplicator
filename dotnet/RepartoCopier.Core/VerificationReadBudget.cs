namespace RepartoCopier.Core;

/// <summary>
/// Global byte budget for post-copy verification reads. The limit is derived from
/// current process/system memory headroom; it has no fixed GiB ceiling. All
/// destination verification workers share one instance per copy job.
/// </summary>
internal sealed class VerificationReadBudget
{
    private const long MinimumUsefulBudget = 32L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Queue<Waiter> _waiters = new();
    private long _usedBytes;
    private long _peakUsedBytes;

    internal VerificationReadBudget(long limitBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitBytes);
        LimitBytes = limitBytes;
    }

    internal long LimitBytes { get; }
    internal long UsedBytes { get { lock (_gate) return _usedBytes; } }
    internal long PeakUsedBytes { get { lock (_gate) return _peakUsedBytes; } }

    internal static VerificationReadBudget CreateForSystem()
    {
        var memory = GC.GetGCMemoryInfo();
        var totalAvailable = Math.Max(MinimumUsefulBudget, memory.TotalAvailableMemoryBytes);
        var normalTarget = Math.Max(MinimumUsefulBudget, totalAvailable / 8);

        var headroom = memory.HighMemoryLoadThresholdBytes > memory.MemoryLoadBytes
            ? memory.HighMemoryLoadThresholdBytes - memory.MemoryLoadBytes
            : MinimumUsefulBudget;
        var pressureTarget = Math.Max(MinimumUsefulBudget, headroom / 2);
        return new VerificationReadBudget(Math.Min(normalTarget, pressureTarget));
    }

    internal ValueTask<Lease> AcquireAsync(int bytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_waiters.Count == 0 && CanGrantLocked(bytes))
            {
                GrantLocked(bytes);
                return ValueTask.FromResult(new Lease(this, bytes));
            }

            var waiter = new Waiter(bytes);
            _waiters.Enqueue(waiter);
            return new ValueTask<Lease>(WaitForLeaseAsync(waiter, token));
        }
    }

    private bool CanGrantLocked(int bytes) =>
        _usedBytes == 0 || _usedBytes + bytes <= LimitBytes;

    private void GrantLocked(int bytes)
    {
        _usedBytes = checked(_usedBytes + bytes);
        _peakUsedBytes = Math.Max(_peakUsedBytes, _usedBytes);
    }

    private async Task<Lease> WaitForLeaseAsync(Waiter waiter, CancellationToken token)
    {
        try
        {
            await waiter.Ready.Task.WaitAsync(token).ConfigureAwait(false);
            return new Lease(this, waiter.Bytes);
        }
        catch
        {
            List<Waiter>? ready;
            lock (_gate)
            {
                if (waiter.Granted)
                {
                    waiter.Granted = false;
                    ReleaseLocked(waiter.Bytes);
                }
                else
                {
                    waiter.Cancelled = true;
                }
                ready = PumpLocked();
            }
            Complete(ready);
            throw;
        }
    }

    private void Release(int bytes)
    {
        List<Waiter>? ready;
        lock (_gate)
        {
            ReleaseLocked(bytes);
            ready = PumpLocked();
        }
        Complete(ready);
    }

    private void ReleaseLocked(int bytes)
    {
        if (bytes <= 0 || _usedBytes < bytes)
            throw new InvalidOperationException("La contabilidad de memoria de verificación quedó inválida.");
        _usedBytes -= bytes;
    }

    private List<Waiter>? PumpLocked()
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
            if (!CanGrantLocked(waiter.Bytes))
                break;
            _waiters.Dequeue();
            GrantLocked(waiter.Bytes);
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
            waiter.Ready.TrySetResult();
    }

    private sealed class Waiter(int bytes)
    {
        internal int Bytes { get; } = bytes;
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cancelled { get; set; }
        internal bool Granted { get; set; }
    }

    internal sealed class Lease : IDisposable
    {
        private VerificationReadBudget? _owner;
        private readonly int _bytes;

        internal Lease(VerificationReadBudget owner, int bytes)
        {
            _owner = owner;
            _bytes = bytes;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_bytes);
        }
    }
}
