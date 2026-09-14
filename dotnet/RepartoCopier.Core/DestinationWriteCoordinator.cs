using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Issues one destination payload as independent explicit-offset writes.
/// Every physical sub-write acquires exactly one scheduler slot immediately
/// before I/O and releases it immediately after completion. No batch reserves
/// idle slots while waiting for the rest of a QD group.
/// </summary>
internal static class DestinationWriteCoordinator
{
    internal static async Task<int> WriteAsync(
        SafeFileHandle handle,
        ReadOnlyMemory<byte> data,
        long baseOffset,
        int requestedDepth,
        int minimumSliceBytes,
        DeviceScheduler scheduler,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumSliceBytes);
        if (data.IsEmpty)
            return 0;

        var maximumUsefulDepth = Math.Max(1, data.Length / minimumSliceBytes);
        var depth = Math.Min(requestedDepth, maximumUsefulDepth);
        depth = Math.Min(depth, scheduler.MaxOutstandingIo);
        if (depth <= 1)
        {
            await WriteSliceAsync(handle, data, baseOffset, scheduler, token).ConfigureAwait(false);
            return 1;
        }

        var tasks = new Task[depth];
        var consumed = 0;
        for (var index = 0; index < depth; index++)
        {
            var slicesRemaining = depth - index;
            var bytesRemaining = data.Length - consumed;
            var length = index == depth - 1
                ? bytesRemaining
                : bytesRemaining / slicesRemaining;
            var slice = data.Slice(consumed, length);
            var offset = checked(baseOffset + consumed);
            tasks[index] = WriteSliceAsync(handle, slice, offset, scheduler, token);
            consumed += length;
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return depth;
    }

    private static async Task WriteSliceAsync(
        SafeFileHandle handle,
        ReadOnlyMemory<byte> data,
        long offset,
        DeviceScheduler scheduler,
        CancellationToken token)
    {
        using var io = await scheduler.AcquireIoAsync(token).ConfigureAwait(false);
        await ExplicitOffsetWriter.WriteOneAsync(handle, data, offset, token).ConfigureAwait(false);
    }
}
