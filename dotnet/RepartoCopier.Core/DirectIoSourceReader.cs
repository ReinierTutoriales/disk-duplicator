using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

internal static class DirectIoSourceReader
{
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagSequentialScan = 0x08000000;

    internal static bool IsEligible(StorageDeviceInfo device, int transferSize) =>
        IsEligibleCore(device, transferSize, solidStateOnly: true);

    internal static bool IsVerificationEligible(StorageDeviceInfo device, int transferSize) =>
        IsEligibleCore(device, transferSize, solidStateOnly: false);

    private static bool IsEligibleCore(StorageDeviceInfo device, int transferSize, bool solidStateOnly)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!OperatingSystem.IsWindows() || transferSize <= 0 || device.IsNetwork || !device.ProbeSucceeded)
            return false;
        if (StorageDeviceIdentity.ConfidenceFor(device) != DeviceIdentityConfidence.Exact)
            return false;
        if (solidStateOnly && device.MediaKind != StorageMediaKind.SolidState)
            return false;
        if (!device.HasKnownSectorAlignment)
            return false;

        var alignment = RequiredAlignment(device);
        return alignment is >= 512 and <= 64 * 1024 &&
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

    internal static bool TryOpen(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        out Session? session) =>
        TryOpenCore(path, device, transferSize, verification: false, out session);

    internal static bool TryOpenForVerification(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        out Session? session) =>
        TryOpenCore(path, device, transferSize, verification: true, out session);

    private static bool TryOpenCore(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        bool verification,
        out Session? session)
    {
        session = null;
        var eligible = verification
            ? IsVerificationEligible(device, transferSize)
            : IsEligible(device, transferSize);
        if (!eligible)
            return false;

        var handle = NativeMethods.CreateFileW(
            path,
            GenericRead,
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

        session = new Session(handle, RequiredAlignment(device));
        return true;
    }

    internal static bool IsFallbackable(Exception error) =>
        error is DirectIoReadException direct && direct.NativeErrorCode is
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
            _handle = handle;
            Alignment = alignment;
        }

        internal int Alignment { get; }

        internal int Read(SourceBufferLease buffer, int bytesToRead)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (bytesToRead <= 0 || bytesToRead % Alignment != 0)
                throw new ArgumentOutOfRangeException(nameof(bytesToRead));
            if (!buffer.IsPinned || buffer.Pointer.ToInt64() % Alignment != 0)
                throw new InvalidOperationException("El buffer de Direct I/O no está alineado al sector físico.");

            var handle = _handle ?? throw new ObjectDisposedException(nameof(Session));
            if (!NativeMethods.ReadFile(handle, buffer.Pointer, checked((uint)bytesToRead), out var read, IntPtr.Zero))
            {
                var code = Marshal.GetLastWin32Error();
                throw new DirectIoReadException(code, new Win32Exception(code).Message);
            }
            return checked((int)read);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _handle, null)?.Dispose();
        }
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

        [DllImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadFile(
            SafeFileHandle file,
            IntPtr buffer,
            uint bytesToRead,
            out uint bytesRead,
            IntPtr overlapped);
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
