namespace RepartoCopier.Core;

internal enum FanoutSpillState
{
    Normal,
    Spill,
    Failed,
}

/// <summary>
/// Per-destination flow state. It isolates memory pressure without throttling
/// the device scheduler: queue depth remains the fixed hardware-class ceiling.
/// Spill entry requires real backlog pressure; recovery uses hysteresis and
/// requires all private payloads to have drained.
/// </summary>
internal sealed class FanoutSpillController
{
    private const int RecoveryNumerator = 1;
    private const int RecoveryDenominator = 2;
    private int _state;

    internal FanoutSpillState State => (FanoutSpillState)Volatile.Read(ref _state);

    internal bool ShouldSpill(long queuedBytes, long backlogTargetBytes, long spillBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(queuedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlogTargetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(spillBytes);

        var state = State;
        if (state == FanoutSpillState.Failed)
            return false;

        if (state == FanoutSpillState.Spill)
        {
            var recoveryThreshold = backlogTargetBytes * RecoveryNumerator / RecoveryDenominator;
            if (spillBytes == 0 && queuedBytes <= recoveryThreshold)
            {
                Interlocked.CompareExchange(
                    ref _state,
                    (int)FanoutSpillState.Normal,
                    (int)FanoutSpillState.Spill);
                return State == FanoutSpillState.Spill;
            }
            return true;
        }

        if (queuedBytes < backlogTargetBytes)
            return false;

        Interlocked.CompareExchange(
            ref _state,
            (int)FanoutSpillState.Spill,
            (int)FanoutSpillState.Normal);
        return State == FanoutSpillState.Spill;
    }

    internal void Fail() => Interlocked.Exchange(ref _state, (int)FanoutSpillState.Failed);
}
