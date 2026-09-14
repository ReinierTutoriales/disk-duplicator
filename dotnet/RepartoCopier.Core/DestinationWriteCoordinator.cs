using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Issues one logical FAN-OUT payload as one explicit-offset write.
/// Concurrency belongs between independent blocks and destination branches;
/// a large sequential block is never fragmented merely to manufacture queue depth.
/// </summary>
internal static class DestinationWriteCoordinator
{
    internal static async Task<int> WriteAsync(
        SafeFileHandle handle,
        ReadOnlyMemory<byte> data,
        long baseOffset,
        DeviceScheduler scheduler,
        CancellationToken token,
        int requiredAlignment = 1)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        if (requiredAlignment <= 0 || (requiredAlignment & (requiredAlignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(requiredAlignment));
        if (requiredAlignment > 1 && (baseOffset % requiredAlignment != 0 || data.Length % requiredAlignment != 0))
            throw new ArgumentException("La escritura Direct I/O debe comenzar y terminar en límites de sector.");
        if (data.IsEmpty)
            return 0;

        using var io = await scheduler.AcquireIoAsync(data.Length, token).ConfigureAwait(false);
        await ExplicitOffsetWriter.WriteOneAsync(handle, data, baseOffset, token).ConfigureAwait(false);
        return 1;
    }
}
