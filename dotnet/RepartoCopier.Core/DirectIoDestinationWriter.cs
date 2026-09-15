using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Unbuffered overlapped destination writer. The caller supplies the same aligned
/// FAN-OUT payload shared by every destination. Only an unaligned final sector is
/// staged into a tiny aligned scratch buffer; whole blocks are never copied per
/// destination.
/// </summary>
internal static class DirectIoDestinationWriter
{
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOverlapped = 0x40000000;

    internal static bool IsEligible(StorageDeviceInfo device, long fileSize)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!OperatingSystem.IsWindows() || fileSize <= 0 || device.IsNetwork || !device.ProbeSucceeded)
            return false;
        if (!device.HasKnownSectorAlignment)
            return false;

        var alignment = DirectIoSourceReader.RequiredAlignment(device);
        return alignment >= 512 && IsPowerOfTwo(alignment);
    }

    internal static bool TryOpen(string path, StorageDeviceInfo device, long fileSize, out Session? session)
    {
        session = null;
        if (!IsEligible(device, fileSize))
            return false;

        var handle = NativeMethods.CreateFileW(
            path,
            GenericWrite,
            FileShare.None,
            IntPtr.Zero,
            OpenExisting,
            FileFlagNoBuffering | FileFlagSequentialScan | FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        session = new Session(handle, DirectIoSourceReader.RequiredAlignment(device));
        return true;
    }

    internal static bool IsFallbackable(Exception error) =>
        error is DirectIoWriteException direct && direct.NativeErrorCode is
            1 or   // ERROR_INVALID_FUNCTION
            5 or   // ERROR_ACCESS_DENIED
            50 or  // ERROR_NOT_SUPPORTED
            87;    // ERROR_INVALID_PARAMETER

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    internal sealed class Session : IDisposable
    {
        private SafeFileHandle? _handle;

        internal Session(SafeFileHandle handle, int alignment)
        {
            ArgumentNullException.ThrowIfNull(handle);
            if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(alignment));
            _handle = handle;
            Alignment = alignment;
        }

        internal int Alignment { get; }

        internal async Task<int> WriteAsync(
            ReadOnlyMemory<byte> data,
            long fileOffset,
            long logicalFileLength,
            bool payloadIsAligned,
            DeviceScheduler scheduler,
            CancellationToken token)
        {
            if (data.IsEmpty)
                return 0;
            if (fileOffset < 0 || fileOffset % Alignment != 0)
                throw new DirectIoWriteException(87, "El offset no está alineado al sector físico.");
            if (!payloadIsAligned)
                throw new DirectIoWriteException(87, "El payload FAN-OUT no está alineado para Direct I/O.");
            if (logicalFileLength < 0 || fileOffset + data.Length > logicalFileLength)
                throw new ArgumentOutOfRangeException(nameof(logicalFileLength));

            var handle = _handle ?? throw new ObjectDisposedException(nameof(Session));
            var alignedLength = data.Length - data.Length % Alignment;
            var operations = 0;
            try
            {
                if (alignedLength > 0)
                {
                    operations += await DestinationWriteCoordinator.WriteAsync(
                        handle,
                        data[..alignedLength],
                        fileOffset,
                        scheduler,
                        token,
                        Alignment).ConfigureAwait(false);
                }

                var tailLength = data.Length - alignedLength;
                if (tailLength > 0)
                {
                    if (fileOffset + data.Length != logicalFileLength)
                        throw new DirectIoWriteException(87, "Solo el tail final puede requerir padding de sector.");

                    using var tail = SourceBufferLease.RentAligned(Alignment, Alignment);
                    tail.Memory.Span.Clear();
                    data.Span[alignedLength..].CopyTo(tail.Memory.Span);
                    operations += await DestinationWriteCoordinator.WriteAsync(
                        handle,
                        tail.Memory,
                        checked(fileOffset + alignedLength),
                        scheduler,
                        token,
                        Alignment).ConfigureAwait(false);
                }

                return operations;
            }
            catch (IOException ex) when (ex is not DirectIoWriteException)
            {
                throw new DirectIoWriteException(ex.HResult & 0xFFFF, ex.Message);
            }
        }

        internal void FinalizeLength(long exactLength)
        {
            var handle = _handle ?? throw new ObjectDisposedException(nameof(Session));
            try
            {
                RandomAccess.SetLength(handle, exactLength);
            }
            catch (IOException ex)
            {
                throw new DirectIoWriteException(ex.HResult & 0xFFFF, ex.Message);
            }
        }

        internal void FlushToDisk()
        {
            var handle = _handle ?? throw new ObjectDisposedException(nameof(Session));
            try
            {
                RandomAccess.FlushToDisk(handle);
            }
            catch (IOException ex)
            {
                throw new DirectIoWriteException(ex.HResult & 0xFFFF, ex.Message);
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
    }

    internal sealed class DirectIoWriteException : IOException
    {
        internal DirectIoWriteException(int nativeErrorCode, string message)
            : base($"Direct I/O de escritura falló ({nativeErrorCode}): {message}") => NativeErrorCode = nativeErrorCode;

        internal int NativeErrorCode { get; }
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);
    }
}
