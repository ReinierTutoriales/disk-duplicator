namespace RepartoCopier.Core;

internal enum FanoutSpillState
{
    Normal,
    Spill,
    Failed,
}

/// <summary>
/// Per-destination flow state. Queue depth remains the fixed hardware-class
/// ceiling. Ordinary recovery requires private payloads to drain and uses
/// hysteresis; explicit fallback exits spill when no normal peer remains or
/// a block cannot reserve private capacity. Existing queued blocks retain FIFO.
/// </summary>
internal sealed class FanoutSpillController
{
    private int _state;

    internal FanoutSpillState State => (FanoutSpillState)Volatile.Read(ref _state);

    // Evaluate the whole fanout before committing any state transitions.
    internal bool WouldSpill(long queuedBytes, long backlogTargetBytes, long spillBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(queuedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlogTargetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(spillBytes);

        return State switch
        {
            FanoutSpillState.Failed => false,
            FanoutSpillState.Spill => spillBytes > 0 || queuedBytes > backlogTargetBytes / 2,
            _ => queuedBytes >= backlogTargetBytes,
        };
    }

    internal bool EnterSpill()
    {
        Interlocked.CompareExchange(ref _state, (int)FanoutSpillState.Spill, (int)FanoutSpillState.Normal);
        return State == FanoutSpillState.Spill;
    }

    internal void ExitSpill() =>
        Interlocked.CompareExchange(ref _state, (int)FanoutSpillState.Normal, (int)FanoutSpillState.Spill);

    internal bool ShouldSpill(long queuedBytes, long backlogTargetBytes, long spillBytes)
    {
        if (WouldSpill(queuedBytes, backlogTargetBytes, spillBytes))
            return EnterSpill();
        ExitSpill();
        return false;
    }

    internal void Fail() => Interlocked.Exchange(ref _state, (int)FanoutSpillState.Failed);
}
