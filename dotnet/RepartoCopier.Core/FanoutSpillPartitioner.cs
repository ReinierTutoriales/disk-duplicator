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
            if (candidate.Controller.WouldSpill(
                candidate.QueuedBytes,
                candidate.BacklogTargetBytes,
                candidate.SpillBytes))
                spilling.Add(candidate.Slot);
            else
                normal.Add(candidate.Slot);
        }

        // Spill only protects destinations that can still use the shared pool.
        // With no normal peer, let shared-pool backpressure bound the producer.
        if (normal.Count == 0)
        {
            spilling.Clear();
            foreach (var candidate in active)
            {
                candidate.Controller.ExitSpill();
                normal.Add(candidate.Slot);
            }
        }
        else
        {
            var spillingSlots = spilling.ToHashSet();
            foreach (var candidate in active)
            {
                if (spillingSlots.Contains(candidate.Slot))
                    candidate.Controller.EnterSpill();
                else
                    candidate.Controller.ExitSpill();
            }
        }

        return new FanoutSpillPartitionResult(normal, spilling);
    }
}
