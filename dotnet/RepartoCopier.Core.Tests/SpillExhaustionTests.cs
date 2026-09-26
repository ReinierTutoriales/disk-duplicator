using System.Collections;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SpillExhaustionTests
{
    private const int BlockSize = 4096;
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type WorkerType = typeof(CopyEngine).GetNestedType("DestinationWorker", BindingFlags.NonPublic)!;
    private static object Property(object value, string name) => value.GetType().GetProperty(name, Members)!.GetValue(value)!;
    private static object? Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, Members)!.Invoke(value, args);

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task ExhaustedSpillFallsBackToSharedInFifoOrder(bool globalLimit, bool cancelWhileWaiting)
    {
        using var slowScheduler = new DeviceScheduler("slow", 1, globalLimit ? BlockSize * 4 : BlockSize);
        using var fastScheduler = new DeviceScheduler("fast", 1, BlockSize * 16);
        using var pool = new SharedFanoutBufferPool(BlockSize);
        var budget = new FanoutSpillBudget(globalLimit ? BlockSize : BlockSize * 8);
        var device = new StorageDeviceInfo("test", "test", null, null, "Unknown", StorageMediaKind.Unknown, null, null, null, false, null);
        object Worker(int slot, DeviceScheduler scheduler) => Activator.CreateInstance(WorkerType, Members, null,
            new object[] { "test", slot, new DestinationProgress("test", BlockSize * 2), device, scheduler,
                new AdaptiveControlByteBudget(_ => 65536), budget, (Action)(() => { }), (Func<int>)(() => 2), (Action)(() => Assert.Fail("Destination deactivated")) }, null)!;
        var slow = Worker(0, slowScheduler);
        var fast = Worker(1, fastScheduler);
        var workers = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(WorkerType))!;
        workers.Add(slow);
        workers.Add(fast);
        await using var job = new CopyJob([]);
        var path = Path.GetTempFileName();
        var bytes = Enumerable.Range(0, BlockSize).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        async Task Produce()
        {
            var entryType = typeof(CopyEngine).GetNestedType("FileEntry", BindingFlags.NonPublic)!;
            var entry = Activator.CreateInstance(entryType, Members, null,
                new object[] { path, "source", (long)BlockSize, File.GetLastWriteTimeUtc(path),
                    (File.GetLastWriteTimeUtc(path).Ticks - DateTime.UnixEpoch.Ticks) * 100 }, null)!;
            var method = typeof(CopyEngine).GetMethod("ReadAndFanOutSequentialAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            await ((Task)method.Invoke(null, new object?[] { entry, device, workers, BlockSize, 4096, pool, job, null })!).WaitAsync(TimeSpan.FromSeconds(10));
        }
        static CopyEngine.FanoutBlock Take(object worker)
        {
            var reader = Property(Property(worker, "Channel"), "Reader");
            object?[] args = [null];
            Assert.IsTrue((bool)reader.GetType().GetMethod("TryRead", Members)!.Invoke(reader, args)!);
            return (CopyEngine.FanoutBlock)Property(args[0]!, "Block");
        }
        static void Release(object worker, CopyEngine.FanoutBlock block)
        {
            ((DeviceScheduler)Property(worker, "DeviceScheduler")).ReleaseBacklog(block.Length);
            Call(worker, "ReleasePendingPayload", block.Length);
            Call(worker, "DecrementQueueDepth");
            block.Release();
        }
        slowScheduler.ReserveBacklog((int)slowScheduler.BacklogTargetBytes);
        try
        {
            await Produce();
            Assert.AreEqual(FanoutSpillState.Spill, ((FanoutSpillController)Property(slow, "SpillController")).State);
            Assert.AreEqual((long)BlockSize, budget.UsedBytes);
            if (cancelWhileWaiting)
            {
                Release(slow, Take(slow));
                var pending = Produce();
                Assert.IsFalse(pending.IsCompleted);
                Assert.AreEqual((long)BlockSize, budget.UsedBytes, "Reservation precedes the pool wait.");
                job.RequestCancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
                Assert.AreEqual(0L, budget.UsedBytes);
                Assert.AreEqual(0L, Property(slow, "SpillBytes"));
                Release(fast, Take(fast));
                Assert.AreEqual(0, pool.UsedBytes);
                return;
            }
            Release(fast, Take(fast));
            Assert.AreEqual(0, pool.UsedBytes);

            await Produce();
            Assert.IsTrue((bool)Property(slow, "IsActive"));
            Assert.AreEqual(FanoutSpillState.Normal, ((FanoutSpillController)Property(slow, "SpillController")).State);
            var first = Take(slow);
            var second = Take(slow);
            Assert.IsTrue(first.IsSpill);
            Assert.IsFalse(second.IsSpill);
            CollectionAssert.AreEqual(bytes, first.Memory.ToArray());
            CollectionAssert.AreEqual(bytes, second.Memory.ToArray());
            Release(fast, Take(fast));
            Assert.AreEqual(BlockSize, pool.UsedBytes, "Slow shared reference must retain the page after the fast peer releases it.");
            using var cancel = new CancellationTokenSource();
            var waiting = pool.RentAsync(BlockSize, 4096, 1, cancel.Token).AsTask();
            Assert.IsFalse(waiting.IsCompleted, "Fallback must apply shared-pool backpressure.");
            Release(slow, first);
            Assert.AreEqual(0L, budget.UsedBytes);
            Assert.IsFalse(waiting.IsCompleted);
            Release(slow, second);
            using (var next = await waiting.WaitAsync(TimeSpan.FromSeconds(5))) { }
            Assert.AreEqual(0, pool.UsedBytes);
            Assert.AreEqual(0L, Property(slow, "SpillBytes"));
            Assert.AreEqual(0L, Property(slow, "PendingPayloadBytes"));
            Assert.AreEqual(0L, Property(fast, "PendingPayloadBytes"));
            Assert.AreEqual(0L, fastScheduler.QueuedBytes);
        }
        finally
        {
            slowScheduler.ReleaseBacklog((int)slowScheduler.BacklogTargetBytes);
            File.Delete(path);
        }
        Assert.AreEqual(0L, slowScheduler.QueuedBytes);
    }
}
