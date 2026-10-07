using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>Exact content comparison. One source read per block, early exit per different destination,
/// and a fixed 8 MiB workspace shared by all streams. No metadata-only equality decisions.</summary>
internal static class ExistingContentComparer
{
    internal const int WorkspaceBytes = 8 * 1024 * 1024;

    internal static async Task<bool[]> CompareAsync(
        string source,
        IReadOnlyList<string> destinations,
        long expectedLength,
        CancellationToken token,
        Func<CancellationToken, ValueTask> waitIfPaused,
        Action<int, int> onRead)
    {
        if (expectedLength < 0) throw new ArgumentOutOfRangeException(nameof(expectedLength));
        if (destinations.Count > CopyPlan.MaxDestinations)
            throw new ArgumentOutOfRangeException(nameof(destinations));
        var equal = new bool[destinations.Count];
        if (destinations.Count == 0) return equal;
        using var sourceHandle = Open(source);
        if (RandomAccess.GetLength(sourceHandle) != expectedLength)
            throw new IOException("El origen cambió de tamaño antes de comparar.");
        var handles = new SafeFileHandle?[destinations.Count];
        byte[]? workspace = null;
        try
        {
            for (var index = 0; index < destinations.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                handles[index] = Open(destinations[index]);
                equal[index] = RandomAccess.GetLength(handles[index]!) == expectedLength;
                if (!equal[index])
                {
                    handles[index]!.Dispose();
                    handles[index] = null;
                }
            }
            workspace = ArrayPool<byte>.Shared.Rent(WorkspaceBytes);
            var perStream = WorkspaceBytes / (destinations.Count + 1);
            long offset = 0;
            while (offset < expectedLength && equal.Any(value => value))
            {
                token.ThrowIfCancellationRequested();
                await waitIfPaused(token).ConfigureAwait(false);
                var amount = (int)Math.Min(perStream, expectedLength - offset);
                var sourceMemory = workspace.AsMemory(0, amount);
                var reads = new List<Task>(destinations.Count + 1)
                {
                    ReadExactlyAsync(sourceHandle, sourceMemory, offset, token, waitIfPaused, null),
                };
                for (var index = 0; index < destinations.Count; index++)
                {
                    if (!equal[index]) continue;
                    var slot = index;
                    reads.Add(ReadExactlyAsync(
                        handles[index]!, workspace.AsMemory((index + 1) * perStream, amount),
                        offset, token, waitIfPaused, bytes => onRead(slot, bytes)));
                }
                // Always drain every read before reusing or returning the shared workspace.
                await Task.WhenAll(reads).ConfigureAwait(false);
                for (var index = 0; index < destinations.Count; index++)
                {
                    if (!equal[index]) continue;
                    if (!sourceMemory.Span.SequenceEqual(
                        workspace.AsSpan((index + 1) * perStream, amount)))
                    {
                        equal[index] = false;
                        handles[index]!.Dispose();
                        handles[index] = null;
                    }
                }
                offset += amount;
            }
            return equal;
        }
        finally
        {
            foreach (var handle in handles) handle?.Dispose();
            if (workspace is not null) ArrayPool<byte>.Shared.Return(workspace);
        }
    }

    private static SafeFileHandle Open(string path) => File.OpenHandle(
        path, FileMode.Open, FileAccess.Read, FileShare.Read,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task ReadExactlyAsync(
        SafeFileHandle handle, Memory<byte> memory, long offset, CancellationToken token,
        Func<CancellationToken, ValueTask> waitIfPaused, Action<int>? onRead)
    {
        var total = 0;
        while (total < memory.Length)
        {
            token.ThrowIfCancellationRequested();
            await waitIfPaused(token).ConfigureAwait(false);
            var read = await RandomAccess.ReadAsync(
                handle, memory[total..], offset + total, token).ConfigureAwait(false);
            if (read == 0) throw new IOException("Lectura incompleta durante comparación.");
            total += read;
            onRead?.Invoke(read);
        }
    }
}
