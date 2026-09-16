from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
ENGINE = ROOT / "dotnet/RepartoCopier.Core/CopyEngine.cs"
DIRECT = ROOT / "dotnet/RepartoCopier.Core/DirectIoSourceReader.cs"
POOL = ROOT / "dotnet/RepartoCopier.Core/SharedFanoutBufferPool.cs"
TESTS = ROOT / "dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs"
POOL_TESTS = ROOT / "dotnet/RepartoCopier.Core.Tests/SharedFanoutBufferPoolTests.cs"
ARCH_TESTS = ROOT / "dotnet/RepartoCopier.Core.Tests/SharedFanoutPoolArchitectureTests.cs"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


def regex_replace_once(text: str, pattern: str, repl: str, label: str) -> str:
    updated, count = re.subn(pattern, repl, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one regex match, found {count}")
    return updated


pool_source = r'''using System.Runtime.InteropServices;

namespace RepartoCopier.Core;

/// <summary>
/// Fixed, contiguous FAN-OUT data pool inspired by ExtremeCopy's CXCFileDataBuffer:
/// one long-lived pinned chunk, page-granular suballocation, and a reference count
/// per page so a page is reusable only after every destination releases it.
/// </summary>
internal sealed class SharedFanoutBufferPool : IDisposable
{
    internal const int DefaultCapacityBytes = 256 * 1024 * 1024;
    private const int MaxSupportedAlignment = 64 * 1024;

    private readonly object _gate = new();
    private readonly byte[] _buffer;
    private readonly ushort[] _pageReferences;
    private readonly int _baseOffset;
    private readonly int _pageSize;
    private readonly int _capacityBytes;
    private int _cursorPage;
    private int _usedPages;
    private bool _disposed;
    private bool _virtualLocked;
    private TaskCompletionSource _spaceAvailable = NewSignal();

    internal SharedFanoutBufferPool(int capacityBytes = DefaultCapacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        _pageSize = Math.Max(4096, Environment.SystemPageSize);
        if ((_pageSize & (_pageSize - 1)) != 0)
            throw new PlatformNotSupportedException("El tamaño de página del sistema debe ser potencia de dos.");

        _capacityBytes = AlignDown(capacityBytes, _pageSize);
        if (_capacityBytes < _pageSize)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes));

        _buffer = GC.AllocateUninitializedArray<byte>(
            checked(_capacityBytes + MaxSupportedAlignment),
            pinned: true);

        var raw = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, 0).ToInt64();
        var mask = MaxSupportedAlignment - 1L;
        var aligned = (raw + mask) & ~mask;
        _baseOffset = checked((int)(aligned - raw));
        _pageReferences = new ushort[_capacityBytes / _pageSize];

        if (OperatingSystem.IsWindows())
        {
            var pointer = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, _baseOffset);
            _virtualLocked = NativeMethods.VirtualLock(pointer, (nuint)_capacityBytes);
        }
    }

    internal int CapacityBytes => _capacityBytes;
    internal int PageSize => _pageSize;
    internal bool IsVirtualLocked => _virtualLocked;

    internal int UsedBytes
    {
        get
        {
            lock (_gate)
                return checked(_usedPages * _pageSize);
        }
    }

    internal int RemainingBytes => _capacityBytes - UsedBytes;

    internal async ValueTask<Lease> RentAsync(
        int requestedBytes,
        int alignment,
        int references,
        CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(references);
        if (references > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(references));
        if ((alignment & (alignment - 1)) != 0 || alignment > MaxSupportedAlignment)
            throw new ArgumentOutOfRangeException(nameof(alignment));
        if (requestedBytes > _capacityBytes)
            throw new ArgumentOutOfRangeException(nameof(requestedBytes));

        var pageCount = checked((requestedBytes + _pageSize - 1) / _pageSize);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task wait;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (TryAllocateLocked(pageCount, alignment, checked((ushort)references), out var pageIndex))
                {
                    var offset = checked(_baseOffset + pageIndex * _pageSize);
                    var pointer = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, offset);
                    var buffer = SourceBufferLease.BorrowPinned(_buffer, offset, requestedBytes, pointer);
                    return new Lease(this, buffer, pageIndex, pageCount, references);
                }
                wait = _spaceAvailable.Task;
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private bool TryAllocateLocked(
        int pageCount,
        int alignment,
        ushort references,
        out int pageIndex)
    {
        if (pageCount > _pageReferences.Length - _usedPages)
        {
            pageIndex = -1;
            return false;
        }

        if (TryFindRunLocked(_cursorPage, _pageReferences.Length, pageCount, alignment, out pageIndex) ||
            (_cursorPage > 0 && TryFindRunLocked(0, _cursorPage, pageCount, alignment, out pageIndex)))
        {
            for (var page = pageIndex; page < pageIndex + pageCount; page++)
            {
                if (_pageReferences[page] != 0)
                    throw new InvalidOperationException("El allocator FAN-OUT intentó reutilizar una página todavía referenciada.");
                _pageReferences[page] = references;
            }
            _usedPages += pageCount;
            _cursorPage = (pageIndex + pageCount) % _pageReferences.Length;
            return true;
        }

        pageIndex = -1;
        return false;
    }

    private bool TryFindRunLocked(
        int begin,
        int end,
        int pageCount,
        int alignment,
        out int pageIndex)
    {
        var index = begin;
        while (index + pageCount <= end)
        {
            while (index < end && _pageReferences[index] != 0)
                index++;
            if (index + pageCount > end)
                break;

            var pointer = Marshal.UnsafeAddrOfPinnedArrayElement(
                _buffer,
                checked(_baseOffset + index * _pageSize));
            if (pointer.ToInt64() % alignment != 0)
            {
                index++;
                continue;
            }

            var run = 0;
            while (run < pageCount && _pageReferences[index + run] == 0)
                run++;
            if (run == pageCount)
            {
                pageIndex = index;
                return true;
            }
            index += Math.Max(1, run + 1);
        }

        pageIndex = -1;
        return false;
    }

    private void ReleasePages(int pageIndex, int pageCount)
    {
        TaskCompletionSource? signal = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            var releasedAny = false;
            for (var page = pageIndex; page < pageIndex + pageCount; page++)
            {
                var current = _pageReferences[page];
                if (current == 0)
                    throw new InvalidOperationException("Se intentó liberar una página FAN-OUT sin referencias.");
                current--;
                _pageReferences[page] = current;
                if (current == 0)
                {
                    _usedPages--;
                    releasedAny = true;
                }
            }

            if (_usedPages < 0)
                throw new InvalidOperationException("La contabilidad del pool FAN-OUT quedó negativa.");

            if (releasedAny)
            {
                signal = _spaceAvailable;
                _spaceAvailable = NewSignal();
            }
        }
        signal?.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static int AlignDown(int value, int alignment) => value - value % alignment;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        TaskCompletionSource? signal;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_usedPages != 0)
                throw new InvalidOperationException("No se puede liberar el pool FAN-OUT mientras existan páginas referenciadas.");
            _disposed = true;
            signal = _spaceAvailable;
            _spaceAvailable = NewSignal();
        }

        signal.TrySetException(new ObjectDisposedException(nameof(SharedFanoutBufferPool)));
        if (_virtualLocked && OperatingSystem.IsWindows())
        {
            var pointer = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, _baseOffset);
            NativeMethods.VirtualUnlock(pointer, (nuint)_capacityBytes);
            _virtualLocked = false;
        }
    }

    internal sealed class Lease : IDisposable
    {
        private SharedFanoutBufferPool? _owner;
        private SourceBufferLease? _buffer;
        private readonly int _pageIndex;
        private readonly int _pageCount;
        private int _remainingReferences;

        internal Lease(
            SharedFanoutBufferPool owner,
            SourceBufferLease buffer,
            int pageIndex,
            int pageCount,
            int references)
        {
            _owner = owner;
            _buffer = buffer;
            _pageIndex = pageIndex;
            _pageCount = pageCount;
            _remainingReferences = references;
        }

        internal int RemainingReferences => Math.Max(0, Volatile.Read(ref _remainingReferences));
        internal SourceBufferLease Buffer =>
            _buffer ?? throw new ObjectDisposedException(nameof(Lease));
        internal Memory<byte> Memory => Buffer.Memory;
        internal bool IsAlignedFor(int alignment) => Buffer.IsAlignedFor(alignment);

        internal bool ReleaseReference()
        {
            var remaining = Interlocked.Decrement(ref _remainingReferences);
            if (remaining < 0)
                throw new InvalidOperationException("La página FAN-OUT fue liberada más veces que sus referencias asignadas.");

            var owner = Volatile.Read(ref _owner)
                ?? throw new ObjectDisposedException(nameof(Lease));
            owner.ReleasePages(_pageIndex, _pageCount);
            if (remaining != 0)
                return false;

            Interlocked.Exchange(ref _buffer, null)?.Dispose();
            Interlocked.Exchange(ref _owner, null);
            return true;
        }

        public void Dispose()
        {
            while (Volatile.Read(ref _remainingReferences) > 0)
                ReleaseReference();
        }
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualLock(IntPtr address, nuint size);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualUnlock(IntPtr address, nuint size);
    }
}
'''
POOL.write_text(pool_source, encoding="utf-8")

# Extend SourceBufferLease so the page allocator can lend slices from one pinned chunk
# without returning the shared backing array to ArrayPool or freeing a pin it does not own.
direct = DIRECT.read_text(encoding="utf-8")
direct = replace_once(
    direct,
    "    private readonly int _offset;\n    private readonly int _capacity;\n\n    private SourceBufferLease(byte[] array, int offset, int capacity, GCHandle pin, bool pinned)\n    {\n        _array = array;\n        _offset = offset;\n        _capacity = capacity;\n        _pin = pin;\n        IsPinned = pinned;\n    }",
    "    private readonly int _offset;\n    private readonly int _capacity;\n    private readonly bool _ownsArray;\n    private readonly bool _ownsPin;\n    private readonly IntPtr _externalPointer;\n\n    private SourceBufferLease(\n        byte[] array,\n        int offset,\n        int capacity,\n        GCHandle pin,\n        bool pinned,\n        bool ownsArray = true,\n        bool ownsPin = true,\n        IntPtr externalPointer = default)\n    {\n        _array = array;\n        _offset = offset;\n        _capacity = capacity;\n        _pin = pin;\n        IsPinned = pinned;\n        _ownsArray = ownsArray;\n        _ownsPin = ownsPin;\n        _externalPointer = externalPointer;\n    }",
    "SourceBufferLease ownership fields")

direct = replace_once(
    direct,
    "            if (!IsPinned || !_pin.IsAllocated)\n                throw new InvalidOperationException(\"El buffer no está fijado; no existe un puntero estable para I/O directo.\");\n            return IntPtr.Add(_pin.AddrOfPinnedObject(), _offset);",
    "            if (!IsPinned)\n                throw new InvalidOperationException(\"El buffer no está fijado; no existe un puntero estable para I/O directo.\");\n            if (_externalPointer != IntPtr.Zero)\n                return _externalPointer;\n            if (!_pin.IsAllocated)\n                throw new InvalidOperationException(\"El buffer fijado perdió su handle de pin.\");\n            return IntPtr.Add(_pin.AddrOfPinnedObject(), _offset);",
    "SourceBufferLease external pointer")

direct = replace_once(
    direct,
    "    internal static SourceBufferLease OwnPooled(byte[] array, int capacity)\n    {",
    "    internal static SourceBufferLease BorrowPinned(byte[] array, int offset, int capacity, IntPtr pointer)\n    {\n        ArgumentNullException.ThrowIfNull(array);\n        if (offset < 0 || capacity <= 0 || offset + capacity > array.Length)\n            throw new ArgumentOutOfRangeException(nameof(capacity));\n        if (pointer == IntPtr.Zero)\n            throw new ArgumentOutOfRangeException(nameof(pointer));\n        return new SourceBufferLease(\n            array,\n            offset,\n            capacity,\n            default,\n            pinned: true,\n            ownsArray: false,\n            ownsPin: false,\n            externalPointer: pointer);\n    }\n\n    internal static SourceBufferLease OwnPooled(byte[] array, int capacity)\n    {",
    "SourceBufferLease BorrowPinned")

direct = replace_once(
    direct,
    "        if (_pin.IsAllocated) _pin.Free();\n        ArrayPool<byte>.Shared.Return(array);",
    "        if (_ownsPin && _pin.IsAllocated) _pin.Free();\n        if (_ownsArray) ArrayPool<byte>.Shared.Return(array);",
    "SourceBufferLease disposal ownership")
DIRECT.write_text(direct, encoding="utf-8")

engine = ENGINE.read_text(encoding="utf-8")
engine = replace_once(
    engine,
    "    private const long SharedFanoutPoolBytes = 64L * 1024 * 1024;",
    "    private const long SharedFanoutPoolBytes = 256L * 1024 * 1024;",
    "256 MiB FAN-OUT pool")
engine = replace_once(
    engine,
    "        var bufferBudget = new AdaptiveByteBudget(SharedFanoutPoolBytes, SharedFanoutPoolBytes);",
    "        using var bufferPool = new SharedFanoutBufferPool(checked((int)SharedFanoutPoolBytes));",
    "pool construction")
engine = engine.replace("                    bufferBudget,\n                    deviceSchedulers.SharedSourceScheduler)",
                        "                    bufferPool,\n                    deviceSchedulers.SharedSourceScheduler)")
engine = engine.replace("        AdaptiveByteBudget bufferBudget,\n        DeviceScheduler? sharedSourceScheduler)",
                        "        SharedFanoutBufferPool bufferPool,\n        DeviceScheduler? sharedSourceScheduler)")
engine = engine.replace("                    bufferBudget,\n                    job,\n                    sharedSourceScheduler)",
                        "                    bufferPool,\n                    job,\n                    sharedSourceScheduler)")

old_read_allocation = '''                var budgetStarted = Stopwatch.GetTimestamp();
                await bufferBudget.AcquireAsync(readBufferSize, job.Token).ConfigureAwait(false);
                var budgetElapsed = Stopwatch.GetElapsedTime(budgetStarted);
                job.Telemetry.RecordBufferWait(budgetElapsed);
                job.Telemetry.ObserveBuffer(bufferBudget.UsedBytes, bufferBudget.TargetBytes);

                SourceBufferLease? lease = SourceBufferLease.RentAligned(
                    readBufferSize,
                    transferAlignment);
                var budgetOwned = true;
                int read;
                try
                {
                    var remaining = checked((int)Math.Min(readBufferSize, entry.Size - totalRead));'''
new_read_allocation = '''                active.RemoveAll(worker => !worker.IsActive);
                if (active.Count == 0)
                    return null;

                var reservedReferences = active.Count;
                var poolStarted = Stopwatch.GetTimestamp();
                SharedFanoutBufferPool.Lease? lease = await bufferPool.RentAsync(
                    readBufferSize,
                    transferAlignment,
                    reservedReferences,
                    job.Token).ConfigureAwait(false);
                job.Telemetry.RecordBufferWait(Stopwatch.GetElapsedTime(poolStarted));
                job.Telemetry.ObserveBuffer(bufferPool.UsedBytes, bufferPool.CapacityBytes);

                int read;
                try
                {
                    var remaining = checked((int)Math.Min(readBufferSize, entry.Size - totalRead));'''
engine = replace_once(engine, old_read_allocation, new_read_allocation, "page-pool acquisition")

engine = engine.replace(
    "read = await direct.ReadAsync(lease, readBufferSize, totalRead, job.Token).ConfigureAwait(false);",
    "read = await direct.ReadAsync(lease.Buffer, readBufferSize, totalRead, job.Token).ConfigureAwait(false);")
engine = engine.replace(
    "read = await buffered.ReadAsync(lease.Memory[..remaining], job.Token).ConfigureAwait(false);",
    "read = await buffered.ReadAsync(lease.Memory[..remaining], job.Token).ConfigureAwait(false);")

old_block = '''                    active.RemoveAll(worker => !worker.IsActive);
                    if (active.Count == 0)
                        return null;

                    var block = new SharedBlock(lease, read, readBufferSize, active.Count, bufferBudget);
                    lease = null;
                    budgetOwned = false;
                    var deliveryStarted = Stopwatch.GetTimestamp();'''
new_block = '''                    active.RemoveAll(worker => !worker.IsActive);
                    var releasedBeforeDelivery = reservedReferences - active.Count;
                    for (var released = 0; released < releasedBeforeDelivery; released++)
                        lease.ReleaseReference();
                    if (active.Count == 0)
                    {
                        lease = null;
                        return null;
                    }

                    var block = new SharedBlock(lease, read);
                    lease = null;
                    var deliveryStarted = Stopwatch.GetTimestamp();'''
engine = replace_once(engine, old_block, new_block, "shared block page references")

old_finally = '''                finally
                {
                    lease?.Dispose();
                    if (budgetOwned)
                        bufferBudget.Release(readBufferSize);
                }'''
new_finally = '''                finally
                {
                    lease?.Dispose();
                }'''
engine = replace_once(engine, old_finally, new_finally, "pool lease cleanup")

new_shared_block = r'''    internal sealed class SharedBlock
    {
        private SharedFanoutBufferPool.Lease? _lease;

        internal SharedBlock(SharedFanoutBufferPool.Lease lease, int length)
        {
            ArgumentNullException.ThrowIfNull(lease);
            if (length <= 0 || length > lease.Memory.Length)
                throw new ArgumentOutOfRangeException(nameof(length));
            _lease = lease;
            Length = length;
        }

        public int Length { get; }
        public ReadOnlyMemory<byte> Memory =>
            (_lease ?? throw new ObjectDisposedException(nameof(SharedBlock))).Memory[..Length];

        internal bool IsAlignedFor(int alignment) =>
            (_lease ?? throw new ObjectDisposedException(nameof(SharedBlock))).IsAlignedFor(alignment);

        public void Release()
        {
            var lease = Volatile.Read(ref _lease)
                ?? throw new InvalidOperationException("SharedBlock liberado más veces que referencias asignadas.");
            if (lease.ReleaseReference())
                Interlocked.CompareExchange(ref _lease, null, lease);
        }
    }

'''
engine = regex_replace_once(
    engine,
    r"    internal sealed class SharedBlock\n    \{.*?\n    \}\n\n    internal sealed class ResourceGovernor",
    new_shared_block + "    internal sealed class ResourceGovernor",
    "SharedBlock pool ownership")

engine = regex_replace_once(
    engine,
    r"\n    internal sealed class AdaptiveByteBudget\n    \{.*?\n    \}\n\n    private sealed class DestinationWorker",
    "\n    private sealed class DestinationWorker",
    "remove obsolete AdaptiveByteBudget")

if "AdaptiveByteBudget" in engine:
    raise RuntimeError("AdaptiveByteBudget token remains in CopyEngine after migration")
if "bufferBudget" in engine:
    raise RuntimeError("bufferBudget token remains in CopyEngine after migration")
if "SourceBufferLease.RentAligned" in engine:
    raise RuntimeError("productive copy path still rents per-block aligned arrays")
ENGINE.write_text(engine, encoding="utf-8")

# Retire tests for the old byte-budget implementation and SharedBlock constructor.
tests = TESTS.read_text(encoding="utf-8")n