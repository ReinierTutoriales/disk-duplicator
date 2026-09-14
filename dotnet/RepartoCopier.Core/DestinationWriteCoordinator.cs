using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Issues one destination payload as independent explicit-offset writes.
/// Every physical sub-write acquires exactly one adaptive scheduler lease
/// immediately before I/O and releases it immediately after completion.
/// Requested depth is an exploration width, not a hardware-class cap.
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
        CancellationToken token,
        int requiredAlignment = 1)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumSliceBytes);
        if (requiredAlignment <= 0 || (requiredAlignment & (requiredAlignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(requiredAlignment));
        if (requiredAlignment > 1 && (baseOffset % requiredAlignment != 0 || data.Length % requiredAlignment != 0))
            throw new ArgumentException("La escritura Direct I/O debe comenzar y terminar en límites de sector.");
        if (data.IsEmpty)
            return 0;

        var minimumAlignedSlice = AlignUp(minimumSliceBytes, requiredAlignment);
        var maximumUsefulDepth = Math.Max(1, data.Length / minimumAlignedSlice);
        var depth = Math.Min(requestedDepth, maximumUsefulDepth);
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
                : AlignDown(bytesRemaining / slicesRemaining, requiredAlignment);
            if (length < minimumAlignedSlice && index != depth - 1)
                throw new InvalidOperationException("La política de profundidad produjo un slice menor que el mínimo alineado.");

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
        using var io = await scheduler.AcquireIoAsync(data.Length, token).ConfigureAwait(false);
        await ExplicitOffsetWriter.WriteOneAsync(handle, data, offset, token).ConfigureAwait(false);
    }

    private static int AlignDown(int value, int alignment) =>
        alignment <= 1 ? value : value - value % alignment;

    private static int AlignUp(int value, int alignment)
    {
        if (alignment <= 1)
            return value;
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }
}
