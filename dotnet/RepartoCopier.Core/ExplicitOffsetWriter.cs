using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Buffered destination writes with explicit file offsets. The owning FileStream
/// is used only for lifetime/flush; correctness never depends on FileStream.Position.
/// </summary>
internal static class ExplicitOffsetWriter
{
    internal static ValueTask WriteOneAsync(
        SafeFileHandle handle,
        ReadOnlyMemory<byte> data,
        long offset,
        CancellationToken token) =>
        RandomAccess.WriteAsync(handle, data, offset, token);

    internal static async Task WriteTwoAsync(
        SafeFileHandle handle,
        ReadOnlyMemory<byte> first,
        long firstOffset,
        ReadOnlyMemory<byte> second,
        long secondOffset,
        CancellationToken token)
    {
        var firstWrite = RandomAccess.WriteAsync(handle, first, firstOffset, token);
        var secondWrite = RandomAccess.WriteAsync(handle, second, secondOffset, token);
        await Task.WhenAll(firstWrite.AsTask(), secondWrite.AsTask()).ConfigureAwait(false);
    }
}
