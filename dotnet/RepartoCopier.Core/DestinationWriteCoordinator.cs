using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// One offset-based asynchronous write at a time per physical destination.
/// Different devices retain independent writer loops and schedulers.
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
        await RandomAccess.WriteAsync(handle, data, offset, token).ConfigureAwait(false);
        return 1;
    }
}
