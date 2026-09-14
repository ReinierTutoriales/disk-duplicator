using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// One explicit-offset destination write. Concurrency belongs to
/// DestinationWriteCoordinator so this primitive never hides queue-depth policy.
/// </summary>
internal static class ExplicitOffsetWriter
{
    internal static ValueTask WriteOneAsync(
        SafeFileHandle handle,
        ReadOnlyMemory<byte> data,
        long offset,
        CancellationToken token) =>
        RandomAccess.WriteAsync(handle, data, offset, token);
}
