from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
ENGINE = ROOT / "dotnet/RepartoCopier.Core/CopyEngine.cs"
DIRECT = ROOT / "dotnet/RepartoCopier.Core/DirectIoSourceReader.cs"
TESTS = ROOT / "dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs"
POOL_TESTS = ROOT / "dotnet/RepartoCopier.Core.Tests/SharedFanoutBufferPoolTests.cs"
ARCH_TESTS = ROOT / "dotnet/RepartoCopier.Core.Tests/SharedFanoutPoolArchitectureTests.cs"


def one(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)


def rxone(text, pattern, replacement, label):
    out, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 regex match, got {count}")
    return out

# SourceBufferLease can now borrow a slice from the single pinned pool without
# returning that backing array to ArrayPool or freeing a pin it does not own.
direct = DIRECT.read_text(encoding="utf-8")
direct = one(direct,
'''    private readonly int _offset;
    private readonly int _capacity;

    private SourceBufferLease(byte[] array, int offset, int capacity, GCHandle pin, bool pinned)
    {
        _array = array;
        _offset = offset;
        _capacity = capacity;
        _pin = pin;
        IsPinned = pinned;
    }''',
'''    private readonly int _offset;
    private readonly int _capacity;
    private readonly bool _ownsArray;
    private readonly bool _ownsPin;
    private readonly IntPtr _externalPointer;

    private SourceBufferLease(
        byte[] array,
        int offset,
        int capacity,
        GCHandle pin,
        bool pinned,
        bool ownsArray = true,
        bool ownsPin = true,
        IntPtr externalPointer = default)
    {
        _array = array;
        _offset = offset;
        _capacity = capacity;
        _pin = pin;
        IsPinned = pinned;
        _ownsArray = ownsArray;
        _ownsPin = ownsPin;
        _externalPointer = externalPointer;
    }''', "SourceBufferLease ownership")

direct = one(direct,
'''            if (!IsPinned || !_pin.IsAllocated)
                throw new InvalidOperationException("El buffer no está fijado; no existe un puntero estable para I/O directo.");
            return IntPtr.Add(_pin.AddrOfPinnedObject(), _offset);''',
'''            if (!IsPinned)
                throw new InvalidOperationException("El buffer no está fijado; no existe un puntero estable para I/O directo.");
            if (_externalPointer != IntPtr.Zero)
                return _externalPointer;
            if (!_pin.IsAllocated)
                throw new InvalidOperationException("El buffer fijado perdió su handle de pin.");
            return IntPtr.Add(_pin.AddrOfPinnedObject(), _offset);''', "borrowed pinned pointer")

direct = one(direct,
'''    internal static SourceBufferLease OwnPooled(byte[] array, int capacity)
    {''',
'''    internal static SourceBufferLease BorrowPinned(byte[] array, int offset, int capacity, IntPtr pointer)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (offset < 0 || capacity <= 0 || offset + capacity > array.Length)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (pointer == IntPtr.Zero)
            throw new ArgumentOutOfRangeException(nameof(pointer));
        return new SourceBufferLease(
            array,
            offset,
            capacity,
            default,
            pinned: true,
            ownsArray: false,
            ownsPin: false,
            externalPointer: pointer);
    }

    internal static SourceBufferLease OwnPooled(byte[] array, int capacity)
    {''', "BorrowPinned factory")

direct = one(direct,
'''        if (_pin.IsAllocated) _pin.Free();
        ArrayPool<byte>.Shared.Return(array);''',
'''        if (_ownsPin && _pin.IsAllocated) _pin.Free();
        if (_ownsArray) ArrayPool<byte>.Shared.Return(array);''', "borrowed lease disposal")
DIRECT.write_text(direct, encoding="utf-8")

engine = ENGINE.read_text(encoding="utf-8")
engine = one(engine,
"    private const long SharedFanoutPoolBytes = 64L * 1024 * 1024;",
"    private const long SharedFanoutPoolBytes = 256L * 1024 * 1024;",
"256 MiB pool")
engine = one(engine,
"        var bufferBudget = new AdaptiveByteBudget(SharedFanoutPoolBytes, SharedFanoutPoolBytes);",
"        using var bufferPool = new SharedFanoutBufferPool(checked((int)SharedFanoutPoolBytes));",
"pool construction")
engine = engine.replace("                    bufferBudget,\n                    deviceSchedulers.SharedSourceScheduler)",
                        "                    bufferPool,\n                    deviceSchedulers.SharedSourceScheduler)")
engine = engine.replace("        AdaptiveByteBudget bufferBudget,\n        DeviceScheduler? sharedSourceScheduler)",
                        "        SharedFanoutBufferPool bufferPool,\n        DeviceScheduler? sharedSourceScheduler)")
engine = engine.replace("                    bufferBudget,\n                    job,\n                    sharedSourceScheduler)",
                        "                    bufferPool,\n                    job,\n                    sharedSourceScheduler)")

engine = one(engine,
'''                var budgetStarted = Stopwatch.GetTimestamp();
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
                    var remaining = checked((int)Math.Min(readBufferSize, entry.Size - totalRead));''',
'''                active.RemoveAll(worker => !worker.IsActive);
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
                    var remaining = checked((int)Math.Min(readBufferSize, entry.Size - totalRead));''', "page-granular rent")

engine = engine.replace(
"read = await direct.ReadAsync(lease, readBufferSize, totalRead, job.Token).ConfigureAwait(false);",
"read = await direct.ReadAsync(lease.Buffer, readBufferSize, totalRead, job.Token).ConfigureAwait(false);")

engine = one(engine,
'''                    active.RemoveAll(worker => !worker.IsActive);
                    if (active.Count == 0)
                        return null;

                    var block = new SharedBlock(lease, read, readBufferSize, active.Count, bufferBudget);
                    lease = null;
                    budgetOwned = false;
                    var deliveryStarted = Stopwatch.GetTimestamp();''',
'''                    active.RemoveAll(worker => !worker.IsActive);
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
                    var deliveryStarted = Stopwatch.GetTimestamp();''', "page reference handoff")

engine = one(engine,
'''                finally
                {
                    lease?.Dispose();
                    if (budgetOwned)
                        bufferBudget.Release(readBufferSize);
                }''',
'''                finally
                {
                    lease?.Dispose();
                }''', "page lease cleanup")

shared_block = '''    internal sealed class SharedBlock
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
engine = rxone(engine,
r"    internal sealed class SharedBlock\n    \{.*?\n    \}\n\n    internal sealed class ResourceGovernor",
shared_block + "    internal sealed class ResourceGovernor", "SharedBlock")
engine = rxone(engine,
r"\n    internal sealed class AdaptiveByteBudget\n    \{.*?\n    \}\n\n    private sealed class DestinationWorker",
"\n    private sealed class DestinationWorker", "remove AdaptiveByteBudget")

if "AdaptiveByteBudget" in engine or "bufferBudget" in engine:
    raise RuntimeError("obsolete byte-budget path remains")
if "SourceBufferLease.RentAligned" in engine:
    raise RuntimeError("copy hot path still rents per-block buffers")
ENGINE.write_text(engine, encoding="utf-8")

# Remove tests that only protected the retired byte-budget implementation.
tests = TESTS.read_text(encoding="utf-8")
for name in ["AdaptiveByteBudgetCancellationReturnsGrantedBytes", "SharedBlockRejectsReferenceOverRelease"]:
    pattern = rf"\n    \[TestMethod\]\n    public (?:async Task|void) {name}\(\)\n    \{{.*?\n    \}}\n"
    tests, count = re.subn(pattern, "\n", tests, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"could not retire obsolete test {name}")
TESTS.write_text(tests, encoding="utf-8")

POOL_TESTS.write_text(r'''using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SharedFanoutBufferPoolTests
{
    [TestMethod]
    public async Task PagesRecycleOnlyAfterEveryDestinationReleases()
    {
        using var pool = new SharedFanoutBufferPool(4 * Environment.SystemPageSize);
        var bytes = 2 * Environment.SystemPageSize;
        var lease = await pool.RentAsync(bytes, Environment.SystemPageSize, 3, CancellationToken.None);
        Assert.AreEqual(bytes, pool.UsedBytes);
        Assert.AreEqual(3, lease.RemainingReferences);

        Assert.IsFalse(lease.ReleaseReference());
        Assert.AreEqual(bytes, pool.UsedBytes);
        Assert.IsFalse(lease.ReleaseReference());
        Assert.AreEqual(bytes, pool.UsedBytes);
        Assert.IsTrue(lease.ReleaseReference());
        Assert.AreEqual(0, pool.UsedBytes);
    }

    [TestMethod]
    public async Task FullPoolBackpressuresUntilReferencedPagesReturn()
    {
        var page = Environment.SystemPageSize;
        using var pool = new SharedFanoutBufferPool(2 * page);
        var first = await pool.RentAsync(2 * page, page, 1, CancellationToken.None);
        var blocked = pool.RentAsync(page, page, 1, CancellationToken.None).AsTask();
        Assert.IsFalse(blocked.IsCompleted);

        Assert.IsTrue(first.ReleaseReference());
        var second = await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(page, pool.UsedBytes);
        Assert.IsTrue(second.ReleaseReference());
        Assert.AreEqual(0, pool.UsedBytes);
    }

    [TestMethod]
    public async Task LeasesArePinnedAndRespectDirectIoAlignment()
    {
        var page = Environment.SystemPageSize;
        using var pool = new SharedFanoutBufferPool(8 * page);
        var alignment = Math.Min(64 * 1024, Math.Max(page, 4096));
        var lease = await pool.RentAsync(page, alignment, 1, CancellationToken.None);
        Assert.IsTrue(lease.Buffer.IsPinned);
        Assert.IsTrue(lease.IsAlignedFor(alignment));
        Assert.IsTrue(lease.ReleaseReference());
    }
}
''', encoding="utf-8")

ARCH_TESTS.write_text(r'''using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SharedFanoutPoolArchitectureTests
{
    [TestMethod]
    public void ProductiveFanoutUsesSingle256MiBPageReferencedPool()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        var pool = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "SharedFanoutBufferPool.cs"));

        StringAssert.Contains(engine, "SharedFanoutPoolBytes = 256L * 1024 * 1024");
        StringAssert.Contains(engine, "new SharedFanoutBufferPool");
        StringAssert.Contains(engine, "SharedFanoutBlockBytes = 8 * 1024 * 1024");
        StringAssert.DoesNotContain(engine, "AdaptiveByteBudget");
        StringAssert.DoesNotContain(engine, "SourceBufferLease.RentAligned");
        StringAssert.Contains(pool, "GC.AllocateUninitializedArray<byte>");
        StringAssert.Contains(pool, "pinned: true");
        StringAssert.Contains(pool, "VirtualLock");
        StringAssert.Contains(pool, "_pageReferences");
        StringAssert.Contains(pool, "ReleaseReference");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
''', encoding="utf-8")

print("shared FAN-OUT pool migration applied")
