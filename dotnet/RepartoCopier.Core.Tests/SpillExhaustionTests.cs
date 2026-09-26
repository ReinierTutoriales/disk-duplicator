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
        // Contract: with a detach-eligible source (local solid state), once a slow
        // destination exhausts its spill ceiling while a normal peer remains, the producer
        // keeps feeding the fast peer to EOF. The slow destination is detached at the first
        // undelivered byte, for the rest of the job, and never retains shared-pool pages
        // (pool = 1 block here, so any retained page stalls RentAsync).
        const int Blocks = 64;
        const int SpillBlocks = 8;
        const long FileBytes = (long)Blocks * BlockSize;
        using var slowScheduler = new DeviceScheduler("slow", 1, BlockSize * SpillBlocks);
        using var fastScheduler = new DeviceScheduler("fast", 1, BlockSize * 16);
        using var pool = new SharedFanoutBufferPool(BlockSize);
        var budget = new FanoutSpillBudget(BlockSize * SpillBlocks);
        var device = new StorageDeviceInfo("test", "test", null, null, "NVMe", StorageMediaKind.SolidState, false, null, null, false, null, IsNetwork: false);
        object Worker(int slot, DeviceScheduler scheduler) => Activator.CreateInstance(WorkerType, Members, null,
            new object[] { "test", slot, new DestinationProgress("test", (ulong)(FileBytes * 2)), device, scheduler,
                new AdaptiveControlByteBudget(_ => 65536), budget, (Action)(() => { }), (Func<int>)(() => 2), (Action)(() => Assert.Fail("Destination deactivated")) }, null)!;
        var slow = Worker(0, slowScheduler);
        var fast = Worker(1, fastScheduler);
        var workers = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(WorkerType))!;
        workers.Add(slow);
        workers.Add(fast);
        await using var job = new CopyJob([]);
        var firstPath = Path.GetTempFileName();
        var secondPath = Path.GetTempFileName();
        var first = Enumerable.Range(0, (int)FileBytes).Select(i => (byte)(i * 31 + i / BlockSize)).ToArray();
        var second = Enumerable.Range(0, (int)FileBytes).Select(i => (byte)(i * 17 + 101 + i / BlockSize)).ToArray();
        await File.WriteAllBytesAsync(firstPath, first);
        await File.WriteAllBytesAsync(secondPath, second);

        static object? TryTake(object worker)
        {
            var reader = Property(Property(worker, "Channel"), "Reader");
            object?[] args = [null];
            return (bool)reader.GetType().GetMethod("TryRead", Members)!.Invoke(reader, args)! ? args[0] : null;
        }
        // Control messages travel wrapped in ControlDelivery; data messages travel bare.
        static object Unwrap(object message) =>
            message.GetType().Name == "ControlDelivery" ? Property(message, "Message") : message;
        static CopyEngine.FanoutBlock? BlockOf(object message) =>
            message.GetType().GetProperty("Block", Members)?.GetValue(message) as CopyEngine.FanoutBlock;
        static void Release(object worker, CopyEngine.FanoutBlock block)
        {
            ((DeviceScheduler)Property(worker, "DeviceScheduler")).ReleaseBacklog(block.Length);
            Call(worker, "ReleasePendingPayload", block.Length);
            Call(worker, "DecrementQueueDepth");
            block.Release();
        }
        List<object> DrainSlow()
        {
            var items = new List<object>();
            for (var message = TryTake(slow); message is not null; message = TryTake(slow))
                items.Add(message);
            return items;
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
                    fastUnexpected.Add(Unwrap(message).GetType().Name);
                    continue;
                }
                // Copy, release, then count: a counted block has already returned its page.
                var copy = block.Memory.ToArray();
                Release(fast, block);
                lock (fastBytes)
                {
                    fastBytes.Write(copy);
                    fastBlocks++;
                }
            }
        });

        var entryType = typeof(CopyEngine).GetNestedType("FileEntry", BindingFlags.NonPublic)!;
        var method = typeof(CopyEngine).GetMethod("ReadAndFanOutSequentialAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        async Task Produce(string path, string label)
        {
            var entry = Activator.CreateInstance(entryType, Members, null,
                new object[] { path, "source", FileBytes, File.GetLastWriteTimeUtc(path),
                    (File.GetLastWriteTimeUtc(path).Ticks - DateTime.UnixEpoch.Ticks) * 100 }, null)!;
            var producer = (Task)method.Invoke(null, new object?[] { entry, device, workers, BlockSize, 4096, pool, job, null })!;
            if (await Task.WhenAny(producer, Task.Delay(TimeSpan.FromSeconds(10))) != producer)
            {
                int blocksSeen;
                lock (fastBytes) blocksSeen = fastBlocks;
                var diagnostic = $"Productor bloqueado tras agotar spill + pool ({label}): fastBlocks={blocksSeen}, " +
                    $"pool.UsedBytes={pool.UsedBytes}, spill.UsedBytes={budget.UsedBytes}, " +
                    $"slow.State={((FanoutSpillController)Property(slow, "SpillController")).State}";
                job.RequestCancel();
                try { await producer; } catch (OperationCanceledException) { }
                Assert.Fail(diagnostic);
            }
            await producer;
        }
        async Task WaitForFastBlocks(int expected)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (true)
            {
                lock (fastBytes)
                {
                    if (fastBlocks >= expected) return;
                }
                if (DateTime.UtcNow > deadline) Assert.Fail($"El rápido no recibió {expected} bloques.");
                await Task.Delay(1);
            }
        }

        // Slow destination starts under full backlog pressure and never drains.
        slowScheduler.ReserveBacklog((int)slowScheduler.BacklogTargetBytes);
        var slowItems = new List<object>();
        try
        {
            // File 1: 8 spill blocks, then detach at the first undelivered byte.
            await Produce(firstPath, "archivo 1");
            await WaitForFastBlocks(Blocks);

            Assert.IsTrue((bool)Property(slow, "IsActive"), "El lento no debe fallar.");
            Assert.IsTrue((bool)Property(slow, "IsDetached"), "El lento queda separado tras agotar spill.");
            Assert.AreEqual(0, pool.UsedBytes, "El lento no debe retener páginas del pool compartido.");
            Assert.AreEqual((long)SpillBlocks * BlockSize, budget.UsedBytes,
                "Sólo el spill entregado queda reservado; la reserva del bloque no entregado se libera.");

            var firstItems = DrainSlow();
            slowItems.AddRange(firstItems);
            var firstBlocks = firstItems.Select(BlockOf).Where(block => block is not null).Select(block => block!).ToList();
            Assert.AreEqual(0, firstBlocks.Count(block => !block.IsSpill), "Sin fallback a shared tras agotar spill.");
            Assert.AreEqual(SpillBlocks, firstBlocks.Count, "El lento conserva sólo el spill previo al traspaso.");
            CollectionAssert.AreEqual(
                first.AsSpan(0, SpillBlocks * BlockSize).ToArray(),
                firstBlocks.SelectMany(block => block.Memory.ToArray()).ToArray());
            var firstDetaches = firstItems.Select(Unwrap).Where(message => message.GetType().Name == "DetachMessage").ToList();
            Assert.AreEqual(1, firstDetaches.Count, "Un único traspaso en el archivo 1.");
            Assert.AreSame(firstDetaches[0], Unwrap(firstItems[^1]), "El traspaso va detrás de todos los bloques entregados (FIFO).");
            Assert.AreEqual((long)SpillBlocks * BlockSize, Convert.ToInt64(Property(firstDetaches[0], "Offset")),
                "Offset = primer byte no entregado.");

            // File 2: the slow destination stays detached for the rest of the job.
            await Produce(secondPath, "archivo 2");
            await WaitForFastBlocks(Blocks * 2);
            stopDrain.Cancel();
            await drainer;

            Assert.AreEqual(0, fastUnexpected.Count, "Mensajes inesperados en el rápido: " + string.Join(", ", fastUnexpected));
            Assert.AreEqual(Blocks * 2, fastBlocks, "El rápido debe recibir los dos archivos completos.");
            CollectionAssert.AreEqual(first.Concat(second).ToArray(), fastBytes.ToArray(), "El rápido debe recibir los bytes en orden.");
            Assert.AreEqual(0, pool.UsedBytes, "El lento separado no debe retener páginas en el archivo 2.");

            var secondItems = DrainSlow();
            slowItems.AddRange(secondItems);
            Assert.AreEqual(1, secondItems.Count, "Archivo 2: el lento separado sólo recibe el traspaso.");
            var secondDetach = Unwrap(secondItems[0]);
            Assert.AreEqual("DetachMessage", secondDetach.GetType().Name);
            Assert.AreEqual(0L, Convert.ToInt64(Property(secondDetach, "Offset")), "Archivo 2: lectura independiente desde el inicio.");

            var diagnostics = job.Telemetry.Snapshot();
            Assert.AreEqual(1, diagnostics.DestinationDetaches, "Un traspaso por destino y job, no por archivo.");
            Assert.AreEqual(0, diagnostics.LastDetachedSlot);
            Assert.AreEqual((long)SpillBlocks * BlockSize, diagnostics.LastDetachOffset);
        }
        finally
        {
            stopDrain.Cancel();
            await drainer;
            slowItems.AddRange(DrainSlow());
            foreach (var message in slowItems)
            {
                if (BlockOf(message) is { } block)
                    Release(slow, block);
                else if (message.GetType().Name == "ControlDelivery")
                    Call(message, "ReleaseBudget");
            }
            slowScheduler.ReleaseBacklog((int)slowScheduler.BacklogTargetBytes);
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }
}
