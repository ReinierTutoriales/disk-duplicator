namespace RepartoCopier.Core;

internal enum FanoutSpillPartitionMode
{
    Normal,
    Spill,
}

internal readonly record struct FanoutSpillPartitionCandidate(
    int Slot,
    long QueuedBytes,
    long BacklogTargetBytes,
    long SpillBytes,
    FanoutSpillController Controller);

internal sealed record FanoutSpillPartitionResult(
    IReadOnlyList<int> NormalSlots,
    IReadOnlyList<int> SpillingSlots);

internal static class FanoutSpillPartitioner
{
    internal static FanoutSpillPartitionResult Partition(IReadOnlyList<FanoutSpillPartitionCandidate> active)
    {
        ArgumentNullException.ThrowIfNull(active);

        var normal = new List<int>(active.Count);
        var spilling = new List<int>();
        foreach (var candidate in active)
        {
            if (candidate.Controller.ShouldSpill(
                candidate.QueuedBytes,
                candidate.BacklogTargetBytes,
                candidate.SpillBytes))
                spilling.Add(candidate.Slot);
            else
                normal.Add(candidate.Slot);
        }

        return new FanoutSpillPartitionResult(normal, spilling);
    }
}
