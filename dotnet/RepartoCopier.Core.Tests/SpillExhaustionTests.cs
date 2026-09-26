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

    [TestMethod]
    public async Task ProducerFinishesFileWhenSlowDestinationExhaustsSpillAndPool()
    {
        // Contract: once a slow destination exhausts its spill ceiling while a normal
        // peer remains, the producer must keep feeding the fast peer to EOF. The slow
        // destination is detached at the first undelivered byte and must not retain
        // shared-pool pages (pool = 1 block here, so any retained page stalls RentAsync).
        const int Blocks = 64;
        const int SpillBlocks = 8;
        const long FileBytes = (long)Blocks * BlockSize;
        using var slowScheduler = new DeviceScheduler("slow", 1, BlockSize * SpillBlocks);
        using var fastScheduler = new DeviceScheduler("fast", 1, BlockSize * 16);
        using var pool = new SharedFanoutBufferPool(BlockSize);
        var budget = new FanoutSpillBudget(BlockSize * SpillBlocks);
        var device = new StorageDeviceInfo("test", "test", null, null, "Unknown", StorageMediaKind.Unknown, null, null, null, false, null);
        object Worker(int slot, DeviceScheduler scheduler) => Activator.CreateInstance(WorkerType, Members, null,
            new object[] { "test", slot, new DestinationProgress("test", (ulong)FileBytes), device, scheduler,
                new AdaptiveControlByteBudget(_ => 65536), budget, (Action)(() => { }), (Func<int>)(() => 2), (Action)(() => Assert.Fail("Destination deactivated")) }, null)!;
        var slow = Worker(0, slowScheduler);
        var fast = Worker(1, fastScheduler);
        var workers = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(WorkerType))!;
        workers.Add(slow);
        workers.Add(fast);
        await using var job = new CopyJob([]);
        var path = Path.GetTempFileName();
        var bytes = Enumerable.Range(0, (int)FileBytes).Select(i => (byte)(i * 31 + i / BlockSize)).ToArray();
        await File.WriteAllBytesAsync(path, bytes);

        static object? TryTake(object worker)
        {
            var reader = Property(Property(worker, "Channel"), "Reader");
            object?[] args = [null];
            return (bool)reader.GetType().GetMethod("TryRead", Members)!.Invoke(reader, args)! ? args[0] : null;
        }
        static CopyEngine.FanoutBlock? BlockOf(object message) =>
            message.GetType().GetProperty("Block", Members)?.GetValue(message) as CopyEngine.FanoutBlock;
        static void Release(object worker, CopyEngine.FanoutBlock block)
        {
            ((DeviceScheduler)Property(worker, "DeviceScheduler")).ReleaseBacklog(block.Length);
            Call(worker, "ReleasePendingPayload", block.Length);
            Call(worker, "DecrementQueueDepth");
            block.Release();
        }

        var fastBytes = new MemoryStream();
        var fastBlocks = 0;
        var fastUnexpected = new List<string>();
        using var stopDrain = new CancellationTokenSource();
        var drainer = Task.Run(async () =>
        {
            while (true)
            {
                var message = TryTake(fast);
                if (message is null)
                {
                    if (stopDrain.IsCancellationRequested) return;
                    await Task.Delay(1);
                    continue;
                }
                var block = BlockOf(message);
                if (block is null)
                {
                    fastUnexpected.Add(message.GetType().Name);
                    continue;
                }
                fastBytes.Write(block.Memory.Span);
                fastBlocks++;
                Release(fast, block);
            }
        });

        // Slow destination starts under full backlog pressure and never drains.
        slowScheduler.ReserveBacklog((int)slowScheduler.BacklogTargetBytes);
        var slowItems = new List<object>();
        try
        {
            var entryType = typeof(CopyEngine).GetNestedType("FileEntry", BindingFlags.NonPublic)!;
            var entry = Activator.CreateInstance(entryType, Members, null,
                new object[] { path, "source", FileBytes, File.GetLastWriteTimeUtc(path),
                    (File.GetLastWriteTimeUtc(path).Ticks - DateTime.UnixEpoch.Ticks) * 100 }, null)!;
            var method = typeof(CopyEngine).GetMethod("ReadAndFanOutSequentialAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var producer = (Task)method.Invoke(null, new object?[] { entry, device, workers, BlockSize, 4096, pool, job, null })!;

            var finished = await Task.WhenAny(producer, Task.Delay(TimeSpan.FromSeconds(10))) == producer;
            if (!finished)
            {
                var diagnostic = $"Productor bloqueado tras agotar spill + pool: fastBlocks={fastBlocks}/{Blocks}, " +
                    $"pool.UsedBytes={pool.UsedBytes}, spill.UsedBytes={budget.UsedBytes}, " +
                    $"slow.State={((FanoutSpillController)Property(slow, "SpillController")).State}";
                job.RequestCancel();
                try { await producer; } catch (OperationCanceledException) { }
                Assert.Fail(diagnostic);
            }

            await producer;
            stopDrain.Cancel();
            await drainer;

            Assert.AreEqual(0, fastUnexpected.Count, "Mensajes inesperados en el rápido: " + string.Join(", ", fastUnexpected));
            Assert.AreEqual(Blocks, fastBlocks, "El rápido debe recibir el archivo completo.");
            CollectionAssert.AreEqual(bytes, fastBytes.ToArray(), "El rápido debe recibir los bytes en orden.");
            Assert.IsTrue((bool)Property(slow, "IsActive"), "El lento no debe fallar.");
            Assert.AreEqual(0, pool.UsedBytes, "El lento no debe retener páginas del pool compartido.");
            Assert.AreEqual((long)SpillBlocks * BlockSize, budget.UsedBytes,
                "Sólo el spill entregado queda reservado; la reserva del bloque no entregado se libera.");

            for (var message = TryTake(slow); message is not null; message = TryTake(slow))
                slowItems.Add(message);
            var slowBlocks = slowItems.Select(BlockOf).Where(block => block is not null).Select(block => block!).ToList();
            Assert.AreEqual(0, slowBlocks.Count(block => !block.IsSpill), "Sin fallback a shared tras agotar spill.");
            Assert.AreEqual(SpillBlocks, slowBlocks.Count, "El lento conserva sólo el spill previo al traspaso.");
            CollectionAssert.AreEqual(
                bytes.AsSpan(0, SpillBlocks * BlockSize).ToArray(),
                slowBlocks.SelectMany(block => block.Memory.ToArray()).ToArray());
            var detaches = slowItems.Where(message => message.GetType().Name == "DetachMessage").ToList();
            Assert.AreEqual(1, detaches.Count, "Un único traspaso por archivo.");
            Assert.AreSame(detaches[0], slowItems[^1], "El traspaso va detrás de todos los bloques entregados (FIFO).");
            Assert.AreEqual((long)SpillBlocks * BlockSize, Convert.ToInt64(Property(detaches[0], "Offset")),
                "Offset = primer byte no entregado.");
        }
        finally
        {
            stopDrain.Cancel();
            await drainer;
            for (var message = TryTake(slow); message is not null; message = TryTake(slow))
                slowItems.Add(message);
            foreach (var message in slowItems)
            {
                if (BlockOf(message) is { } block)
                    Release(slow, block);
            }
            slowScheduler.ReleaseBacklog((int)slowScheduler.BacklogTargetBytes);
            File.Delete(path);
        }
    }
}
