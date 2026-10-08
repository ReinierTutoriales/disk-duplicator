using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Stable sequential destination writer modeled after ExtremeCopy's active path:
/// one synchronous physical write at a time per destination, SEQUENTIAL_SCAN and
/// NO_BUFFERING when safe. The shared FAN-OUT payload is never copied per destination.
/// </summary>
internal static class DirectIoDestinationWriter
{
    private const uint GenericWrite = 0x40000000;
    private const uint CreateNew = 1;
    private const int ErrorDiskFull = 112;
    private const int ErrorFileTooLarge = 223;
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

    /// <summary>
    /// Creates the part file directly with NO_BUFFERING and reserves its allocation on that same handle: one
    /// create per file instead of create + preallocate + close + reopen. Returns false (nothing created) when the
    /// volume refuses unbuffered handles, so the caller creates a buffered stream instead.
    /// </summary>
    internal static bool TryOpen(string path, StorageDeviceInfo device, long fileSize, long preallocationSize,
        out Session? session)
    {
        session = null;
        if (!IsEligible(device, fileSize))
            return false;

        var fullPath = Path.GetFullPath(path);
        var handle = NativeMethods.CreateFileW(
            WindowsPath.Extended(fullPath),
            GenericWrite,
            FileShare.Read,
            IntPtr.Zero,
            CreateNew,
            FileFlagNoBuffering | FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == 80) // ERROR_FILE_EXISTS: a stale part; the caller removes it and retries.
                throw new IOException($"El archivo temporal ya existe: {fullPath}", unchecked((int)0x80070050));
            return false;
        }

        try
        {
            Preallocate(handle, preallocationSize, fullPath);
        }
        catch
        {
            handle.Dispose();
            try { File.Delete(fullPath); } catch { }
            throw;
        }
        session = new Session(handle, DirectIoSourceReader.RequiredAlignment(device));
        return true;
    }

    // Same contract as FileStreamOptions.PreallocationSize: reserve clusters (not the end of file), fail only
    // when the volume cannot hold the file, and treat any other refusal as "no preallocation".
    private static void Preallocate(SafeFileHandle handle, long bytes, string path)
    {
        if (bytes <= 0)
            return;
        var allocation = bytes;
        if (NativeMethods.SetFileInformationByHandle(handle, 5 /* FileAllocationInfo */, ref allocation, sizeof(long)))
            return;
        var error = Marshal.GetLastWin32Error();
        if (error is ErrorDiskFull or ErrorFileTooLarge)
            throw new IOException($"No hay espacio para reservar {bytes} bytes en {path}.", unchecked((int)(0x80070000 | (uint)error)));
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
            if (!MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment) || segment.Array is null)
                throw new DirectIoWriteException(87, "El buffer de escritura debe estar respaldado por el pool FAN-OUT fijado.");

            if (!NativeMethods.SetFilePointerEx(handle, offset, out _, 0))
            {
                var code = Marshal.GetLastWin32Error();
                throw new DirectIoWriteException(code, "No se pudo posicionar el handle síncrono del destino.");
            }

            var pointer = Marshal.UnsafeAddrOfPinnedArrayElement(segment.Array, segment.Offset);
            if (!NativeMethods.WriteFile(handle, pointer, checked((uint)data.Length), out var written, IntPtr.Zero))
            {
                var code = Marshal.GetLastWin32Error();
                throw new DirectIoWriteException(code, "WriteFile síncrono falló.");
            }
            if (written != data.Length)
                throw new DirectIoWriteException(1117, $"WriteFile escribió {written} de {data.Length} bytes.");
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

        internal void SetLastWriteTimeUtc(DateTime lastWriteTimeUtc)
        {
            var handle = _handle ?? throw new ObjectDisposedException(nameof(Session));
            File.SetLastWriteTimeUtc(handle, lastWriteTimeUtc);
        }

        internal long Length()
        {
            var handle = _handle ?? throw new ObjectDisposedException(nameof(Session));
            return RandomAccess.GetLength(handle);
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
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFilePointerEx(SafeFileHandle hFile, long distanceToMove, out long newFilePointer, uint moveMethod);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteFile(SafeFileHandle hFile, IntPtr buffer, uint numberOfBytesToWrite, out uint numberOfBytesWritten, IntPtr overlapped);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass, ref long information, uint bufferSize);

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
