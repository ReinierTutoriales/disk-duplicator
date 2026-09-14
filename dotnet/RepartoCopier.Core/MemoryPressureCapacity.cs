namespace RepartoCopier.Core;

/// <summary>
/// Converts the runtime's current high-memory-load boundary into a byte capacity.
/// This deliberately avoids fixed percentages of installed RAM. The engine may
/// consume the currently available headroom up to the runtime/OS pressure
/// boundary and recalculates it on demand as system pressure changes.
/// </summary>
internal static class MemoryPressureCapacity
{
    internal static long GetSafeTotalBytes(long usedBytes, long minimumProgressBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(usedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumProgressBytes);

        var memory = GC.GetGCMemoryInfo();
        var high = memory.HighMemoryLoadThresholdBytes;
        var load = memory.MemoryLoadBytes;

        if (high <= 0)
        {
            var available = Math.Max(minimumProgressBytes, memory.TotalAvailableMemoryBytes);
            return SaturatingAdd(usedBytes, Math.Max(0, available - minimumProgressBytes));
        }

        var headroom = Math.Max(0L, high - Math.Max(0L, load));
        var additional = Math.Max(0L, headroom - minimumProgressBytes);
        var total = SaturatingAdd(usedBytes, additional);
        return Math.Max(Math.Max(usedBytes, minimumProgressBytes), total);
    }

    private static long SaturatingAdd(long left, long right) =>
        right >= long.MaxValue - left ? long.MaxValue : left + right;
}
