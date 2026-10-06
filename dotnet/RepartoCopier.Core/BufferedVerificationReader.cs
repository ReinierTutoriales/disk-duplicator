using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

internal static class BufferedVerificationReader
{
    internal static ValueTask<int> ReadBlockAsync(
        SafeFileHandle handle, Memory<byte> buffer, long offset, CancellationToken token) =>
        FillBlockAsync(handle, buffer, offset,
            static (file, remaining, position, cancellation) =>
                RandomAccess.ReadAsync(file, remaining, position, cancellation), token);

    // A short read is not EOF. Keep the same block and advance only by bytes
    // actually received; zero is the only successful-read EOF indication.
    internal static async ValueTask<int> FillBlockAsync<T>(
        T state, Memory<byte> buffer, long offset,
        Func<T, Memory<byte>, long, CancellationToken, ValueTask<int>> read,
        CancellationToken token)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            var received = await read(state, buffer[total..], checked(offset + total), token).ConfigureAwait(false);
            if (received == 0) break;
            if (received < 0 || received > buffer.Length - total)
                throw new IOException("La lectura de verificación devolvió un tamaño inválido.");
            total += received;
        }
        return total;
    }
}
