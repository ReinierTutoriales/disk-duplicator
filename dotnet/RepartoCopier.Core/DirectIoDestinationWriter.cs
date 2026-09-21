using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Stable sequential destination writer modeled after ExtremeCopy's active path:
/// explicit-offset writes with bounded per-device queue depth, SEQUENTIAL_SCAN and
/// NO_BUFFERING when safe. The shared FAN-OUT payload is never copied per destination.
/// </summary>
internal static class DirectIoDestinationWriter
{
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagSequentialScan = 0x08000000;

    internal static bool IsEligible(StorageDeviceInfo device, long fileSize)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!OperatingSystem.IsWindows() || fileSize <= 0 || device.IsNetwork || !device.ProbeSucceeded)
            return false;
        if (!device.HasKnownSectorAlignment)
            return false;

        var alignment = DirectIoSourceReader.RequiredAlignment(device);
        if (alignment < 512 || !IsPowerOfTwo(alignment))
            return false;

        // ExtremeCopy enables NO_BUFFERING for local files >= 64 KiB, or for
        // smaller files that already end on a physical-sector boundary.
        return fileSize >= 64L * 1024 || fileSize % alignment == 0;
    }

    internal static bool TryOpen(string path, StorageDeviceInfo device, long fileSize, out Session? session)
    {
        session = null;
        if (!IsEligible(device, fileSize))
            return false;

        var handle = NativeMethods.CreateFileW(
            path,
            GenericWrite,
            FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagNoBuffering | FileFlagSequentialScan,
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
        TransientIoErrorClassifier.IsDirectFallbackable(error);

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
                    using var io = await scheduler.AcquireIoAsync(alignedLength, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    WriteSynchronous(handle, data[..alignedLength], fileOffset);
                    operations++;
                }

                var tailLength = data.Length - alignedLength;
                if (tailLength > 0)
                {
                    if (fileOffset + data.Length != logicalFileLength)
                        throw new DirectIoWriteException(87, "Solo el tail final puede requerir padding de sector.");

                    using var tail = SourceBufferLease.RentAligned(Alignment, Alignment);
                    tail.Memory.Span.Clear();
                    data.Span[alignedLength..].CopyTo(tail.Memory.Span);
                    using var io = await scheduler.AcquireIoAsync(Alignment, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    WriteSynchronous(handle, tail.Memory, checked(fileOffset + alignedLength));
                    operations++;
                }

                return operations;
            }
            catch (IOException ex) when (ex is not DirectIoWriteException)
            {
                throw new DirectIoWriteException(TransientIoErrorClassifier.GetNativeCodeOrZero(ex), ex.Message);
            }
        }

        private static void WriteSynchronous(SafeFileHandle handle, ReadOnlyMemory<byte> data, long offset)
        {
            // The destination can have QD > 1. Never mutate the shared file pointer:
            // SetFilePointerEx + WriteFile races concurrent writes on the same handle.
            // RandomAccess supplies an explicit offset per operation and preserves the
            // aligned pinned FAN-OUT buffer required by FILE_FLAG_NO_BUFFERING.
            try
            {
                RandomAccess.Write(handle, data.Span, offset);
            }
            catch (IOException ex)
            {
                throw new DirectIoWriteException(
                    TransientIoErrorClassifier.GetNativeCodeOrZero(ex),
                    ex.Message);
            }
        }

        internal void FinalizeLength(long exactLength)
        {
            var handle = _handle ?? throw new ObjectDisposedException(nameof(Session));
            try { RandomAccess.SetLength(handle, exactLength); }
            catch (IOException ex)
            {
                throw new DirectIoWriteException(TransientIoErrorClassifier.GetNativeCodeOrZero(ex), ex.Message);
            }
        }

        internal void FlushToDisk()
        {
            var handle = _handle ?? throw new ObjectDisposedException(nameof(Session));
            try { RandomAccess.FlushToDisk(handle); }
            catch (IOException ex)
            {
                throw new DirectIoWriteException(TransientIoErrorClassifier.GetNativeCodeOrZero(ex), ex.Message);
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
