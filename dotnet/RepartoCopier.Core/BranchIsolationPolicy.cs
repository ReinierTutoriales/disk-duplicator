namespace RepartoCopier.Core;

internal static class BranchIsolationPolicy
{
    internal static long SharedRetentionTargetBytes(
        int blockBytes,
        int currentQueueDepth,
        long deviceBacklogTargetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(currentQueueDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceBacklogTargetBytes);

        var qdWindow = currentQueueDepth > long.MaxValue / blockBytes
            ? long.MaxValue
            : (long)blockBytes * currentQueueDepth;
        return Math.Max(blockBytes, Math.Min(deviceBacklogTargetBytes, qdWindow));
    }

    internal static bool ShouldDetach(
        long pendingPayloadBytes,
        int blockBytes,
        int currentQueueDepth,
        long deviceBacklogTargetBytes)
    {
        if (pendingPayloadBytes <= 0)
            return false;
        return pendingPayloadBytes > SharedRetentionTargetBytes(
            blockBytes, currentQueueDepth, deviceBacklogTargetBytes);
    }
}
