using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// One stable synchronous write per physical destination. Different physical
/// destinations still run in parallel through their independent writer loops.
/// </summary>
internal static class DestinationWriteCoordinator
{
    internal static async Task<int> WriteAsync(
        SafeFileHandle handle,
        ReadOnlyMemory<byte> data,
        long offset,
        DeviceScheduler scheduler,
        CancellationToken token,
        int alignment = 1)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(scheduler);
        if (data.IsEmpty) return 0;
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (alignment <= 0 || offset % alignment != 0 || data.Length % alignment != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));

        using var lease = await scheduler.AcquireIoAsync(data.Length, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        RandomAccess.Write(handle, data.Span, offset);
        return 1;
    }
}
