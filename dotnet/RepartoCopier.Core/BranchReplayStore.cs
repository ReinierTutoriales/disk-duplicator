using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Append-only replay store for a persistently lagging destination branch.
/// Placement is supplied by BranchReplayPlacement; a null directory disables
/// disk replay rather than risking contention with source/destination devices.
/// </summary>
internal sealed class BranchReplayStore : IDisposable
{
    private readonly object _gate = new();
    private readonly string? _directory;
    private FileStream? _stream;
    private long _nextOffset;
    private bool _disposed;

    internal BranchReplayStore(string? directory) => _directory = directory;

    internal bool IsEnabled => !string.IsNullOrWhiteSpace(_directory);

    internal readonly record struct Segment(long Offset, int Length, uint VerificationCrc32C);

    internal async ValueTask<Segment> SpillAsync(
        ReadOnlyMemory<byte> data,
        uint verificationCrc32,
        CancellationToken token)
    {
        if (data.IsEmpty)
            throw new ArgumentException("Replay payload cannot be empty.", nameof(data));
        if (!IsEnabled)
            throw new InvalidOperationException("Replay store is disabled because no safe physical placement was proven.");

        SafeFileHandle handle;
        long offset;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream ??= OpenStore(_directory!);
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

    private static FileStream OpenStore(string directory)
    {
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