namespace RepartoCopier.Core;

internal sealed class GlobalControlBacklogBudget
{
    private readonly object _gate = new();
    private readonly Queue<Waiter> _waiters = new();
    private readonly int _capacity;
    private int _used;
    private int _peak;

    internal GlobalControlBacklogBudget(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    internal int Capacity => _capacity;

    internal int Used
    {
        get { lock (_gate) return _used; }
    }

    internal int Peak
    {
        get { lock (_gate) return _peak; }
    }

    internal ValueTask AcquireAsync(CancellationToken token)
    {
        lock (_gate)
        {
            if (_waiters.Count == 0 && TryAcquireLocked())
                return ValueTask.CompletedTask;

            var waiter = new Waiter();
            _waiters.Enqueue(waiter);
            return new ValueTask(WaitAsync(waiter, token));
        }
    }

    internal void Release()
    {
        List<Waiter>? ready;
        lock (_gate)
        {
            if (_used <= 0)
                throw new InvalidOperationException("Se intentó liberar un slot de backlog de control no adquirido.");
            _used--;
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
                    if (_used <= 0)
                        throw new InvalidOperationException("Contabilidad del backlog de control inválida durante cancelación.");
                    _used--;
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

    private bool TryAcquireLocked()
    {
        if (_used >= _capacity)
            return false;
        _used++;
        if (_used > _peak)
            _peak = _used;
        return true;
    }

    private List<Waiter>? PumpWaitersLocked()
    {
        List<Waiter>? ready = null;
        while (_used < _capacity && _waiters.Count > 0)
        {
            var waiter = _waiters.Dequeue();
            if (waiter.Cancelled)
                continue;
            waiter.Granted = true;
            _used++;
            if (_used > _peak)
                _peak = _used;
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

    private sealed class Waiter
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cancelled { get; set; }
        internal bool Granted { get; set; }
    }
}
