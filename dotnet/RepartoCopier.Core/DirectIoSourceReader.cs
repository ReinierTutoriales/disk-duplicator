using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

internal static class DirectIoSourceReader
{
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOverlapped = 0x40000000;

    internal const int MaximumSupportedAlignment = 64 * 1024;

    internal static bool IsEligible(StorageDeviceInfo device, int transferSize) =>
        IsEligibleCore(device, transferSize);

    internal static bool IsVerificationEligible(StorageDeviceInfo device, int transferSize) =>
        IsEligibleCore(device, transferSize);

    private static bool IsEligibleCore(StorageDeviceInfo device, int transferSize)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!OperatingSystem.IsWindows() || transferSize <= 0 || device.IsNetwork || !device.ProbeSucceeded)
            return false;
        if (StorageDeviceIdentity.ConfidenceFor(device) != DeviceIdentityConfidence.Exact)
            return false;
        if (!device.HasKnownSectorAlignment)
            return false;

        var alignment = RequiredAlignment(device);
        return alignment is >= 512 and <= MaximumSupportedAlignment &&
               IsPowerOfTwo(alignment) &&
               transferSize % alignment == 0;
    }

    internal static int RequiredAlignment(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var logical = checked((int)(device.LogicalSectorBytes ?? 0));
        var physical = checked((int)(device.PhysicalSectorBytes ?? 0));
        return Math.Max(logical, physical);
    }

    internal static bool TryOpenOverlapped(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        out OverlappedSession? session) =>
        TryOpenOverlappedCore(path, device, transferSize, out session);

    internal static bool TryOpenOverlappedForVerification(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        out OverlappedSession? session) =>
        TryOpenOverlappedCore(path, device, transferSize, out session);

    private static bool TryOpenOverlappedCore(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        out OverlappedSession? session)
    {
        session = null;
        if (!IsEligibleCore(device, transferSize))
            return false;

        var handle = NativeMethods.CreateFileW(
            path,
            GenericRead,
            FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagNoBuffering | FileFlagSequentialScan | FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        try
        {
            var length = RandomAccess.GetLength(handle);
            session = new OverlappedSession(handle, RequiredAlignment(device), length);
            return true;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static bool IsFallbackable(Exception error) =>
        error is DirectIoReadException direct && direct.NativeErrorCode is
            1 or   // ERROR_INVALID_FUNCTION
            5 or   // ERROR_ACCESS_DENIED
            50 or  // ERROR_NOT_SUPPORTED
            87;    // ERROR_INVALID_PARAMETER

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    internal sealed class OverlappedSession : IDisposable
    {
        private SafeFileHandle? _handle;
        private readonly long _length;

        internal OverlappedSession(SafeFileHandle handle, int alignment, long length)
        {
            ArgumentNullException.ThrowIfNull(handle);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            _handle = handle;
            Alignment = alignment;
            _length = length;
        }

        internal int Alignment { get; }
        internal long Length => _length;

        internal async Task<int> ReadAsync(
            SourceBufferLease buffer,
            int bytesToRead,
            long fileOffset,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (bytesToRead <= 0 || bytesToRead % Alignment != 0)
                throw new ArgumentOutOfRangeException(nameof(bytesToRead));
            if (fileOffset < 0 || fileOffset > _length)
                throw new ArgumentOutOfRangeException(nameof(fileOffset));

            // FILE_FLAG_NO_BUFFERING requires sector-aligned offsets, but EOF itself
            // is a logical byte position and is allowed to be unaligned. A final
            // direct read can consume an unaligned file tail, leaving totalRead at
            // an unaligned exact EOF. The next read must terminate here instead of
            // feeding that EOF position into the alignment guard.
            if (fileOffset == _length)
                return 0;

            if (fileOffset % Alignment != 0)
                throw new ArgumentOutOfRangeException(nameof(fileOffset));
            if (!buffer.IsPinned || buffer.Pointer.ToInt64() % Alignment != 0)
                throw new InvalidOperationException("El buffer OVERLAPPED no está alineado al sector físico.");

            var handle = _handle ?? throw new ObjectDisposedException(nameof(OverlappedSession));
            try
            {
                return await RandomAccess.ReadAsync(handle, buffer.Memory[..bytesToRead], fileOffset, token).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                var code = ex.HResult & 0xFFFF;
                throw new DirectIoReadException(code, ex.Message);
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
    }

    internal sealed class DirectIoReadException : IOException
    {
        internal DirectIoReadException(int nativeErrorCode, string message)
            : base($"Direct I/O falló ({nativeErrorCode}): {message}") => NativeErrorCode = nativeErrorCode;

        internal int NativeErrorCode { get; }
    }

    private static class NativeMethods
    {
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

internal sealed class SourceBufferLease : IDisposable
{
    private byte[]? _array;
    private GCHandle _pin;
    private readonly bool _pinned;
    private readonly int _offset;
    private readonly int _capacity;

    private SourceBufferLease(byte[] array, int offset, int capacity, GCHandle pin, bool pinned)
    {
        _array = array;
        _offset = offset;
        _capacity = capacity;
        _pin = pin;
        _pinned = pinned;
    }

    internal bool IsPinned => _pinned && _pin.IsAllocated;
    internal int Capacity => _capacity;

    internal bool IsAlignedFor(int alignment)
    {
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));
        return IsPinned && Pointer.ToInt64() % alignment == 0;
    }

    internal Memory<byte> Memory
    {
        get
        {
            var array = _array ?? throw new ObjectDisposedException(nameof(SourceBufferLease));
            return array.AsMemory(_offset, _capacity);
        }
    }

    internal IntPtr Pointer
    {
        get
        {
            if (!IsPinned)
                throw new InvalidOperationException("El buffer no está fijado para Direct I/O.");
            return IntPtr.Add(_pin.AddrOfPinnedObject(), _offset);
        }
    }

    internal static SourceBufferLease RentBuffered(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        var array = ArrayPool<byte>.Shared.Rent(capacity);
        return new SourceBufferLease(array, 0, capacity, default, pinned: false);
    }

    internal static SourceBufferLease RentAligned(int capacity, int alignment)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));

        var array = ArrayPool<byte>.Shared.Rent(checked(capacity + alignment));
        var pin = GCHandle.Alloc(array, GCHandleType.Pinned);
        try
        {
            var address = pin.AddrOfPinnedObject().ToInt64();
            var remainder = address & (alignment - 1L);
            var offset = remainder == 0 ? 0 : checked((int)(alignment - remainder));
            if (offset + capacity > array.Length)
                throw new InvalidOperationException("ArrayPool devolvió un buffer insuficiente para alineación.");
            return new SourceBufferLease(array, offset, capacity, pin, pinned: true);
        }
        catch
        {
            pin.Free();
            ArrayPool<byte>.Shared.Return(array);
            throw;
        }
    }

    internal static SourceBufferLease OwnPooled(byte[] array, int capacity)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (capacity <= 0 || capacity > array.Length) throw new ArgumentOutOfRangeException(nameof(capacity));
        return new SourceBufferLease(array, 0, capacity, default, pinned: false);
    }

    public void Dispose()
    {
        var array = Interlocked.Exchange(ref _array, null);
        if (array is null)
            return;
        if (_pin.IsAllocated)
            _pin.Free();
        ArrayPool<byte>.Shared.Return(array);
    }
}