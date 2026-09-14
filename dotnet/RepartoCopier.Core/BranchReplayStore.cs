using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Append-only temporary replay store used only by a destination branch that has
/// exceeded its soft physical backlog watermark. It breaks the branch's ownership
/// of source SharedBlock memory while preserving one physical source read.
/// The file is created lazily on the system temporary volume and is delete-on-close.
/// </summary>
internal sealed class BranchReplayStore : IDisposable
{
    private readonly object _gate = new();
    private FileStream? _stream;
    private long _nextOffset;
    private bool _disposed;

    internal readonly record struct Segment(long Offset, int Length, uint VerificationCrc32);

    internal async ValueTask<Segment> SpillAsync(
        ReadOnlyMemory<byte> data,
        uint verificationCrc32,
        CancellationToken token)
    {
        if (data.IsEmpty)
            throw new ArgumentException("Replay payload cannot be empty.", nameof(data));

        SafeFileHandle handle;
        long offset;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream ??= OpenStore();
            handle = _stream.SafeFileHandle;
            offset = _nextOffset;
            _nextOffset = checked(_nextOffset + data.Length);
        }

        await RandomAccess.WriteAsync(handle, data, offset, token).ConfigureAwait(false);
        return new Segment(offset, data.Length, verificationCrc32);
    }

    internal async ValueTask ReadAsync(
        Segment segment,
        Memory<byte> destination,
        CancellationToken token)
    {
        if (destination.Length < segment.Length)
            throw new ArgumentException("Replay destination is smaller than the segment.", nameof(destination));

        SafeFileHandle handle;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            handle = (_stream ?? throw new InvalidOperationException("Replay store has no data.")).SafeFileHandle;
        }

        var consumed = 0;
        while (consumed < segment.Length)
        {
            var read = await RandomAccess.ReadAsync(
                handle,
                destination.Slice(consumed, segment.Length - consumed),
                checked(segment.Offset + consumed),
                token).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Replay store ended before the complete segment was read.");
            consumed += read;
        }
    }

    private static FileStream OpenStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RepartoCopier", "branch-replay");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Environment.ProcessId}-{Guid.NewGuid():N}.replay");
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read,
            BufferSize = 1,
            Options = FileOptions.Asynchronous | FileOptions.RandomAccess | FileOptions.DeleteOnClose,
        });
    }

    public void Dispose()
    {
        FileStream? stream;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            stream = _stream;
            _stream = null;
        }
        stream?.Dispose();
    }
}
