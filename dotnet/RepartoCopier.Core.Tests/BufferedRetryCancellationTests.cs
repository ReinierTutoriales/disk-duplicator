using System.Reflection;
using Microsoft.Win32.SafeHandles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class BufferedRetryCancellationTests
{
    [TestMethod]
    public async Task CancellationAtTransientRetryReleasesProductionBranchAndPool()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repartocopier-retry-{Guid.NewGuid():N}.part");
        var bytes = Environment.SystemPageSize;
        using var pool = new SharedFanoutBufferPool(bytes);
        var lease = await pool.RentAsync(bytes, bytes, 1, CancellationToken.None);
        using var scheduler = new DeviceScheduler("retry-test", 1, bytes);
        var progress = new DestinationProgress(path, (ulong)bytes, 1);
        await using var job = new CopyJob([progress]);
        try
        {
            // SafeFileHandle is virtual: inject a real-classified transient error
            // exactly when cancellation begins, without production test hooks.
            using var stream = new CancelAndFailStream(path, job);
            var engine = typeof(CopyEngine);
            var device = new StorageDeviceInfo(path, Path.GetPathRoot(path)!, null, null,
                "Unknown", StorageMediaKind.Unknown, null, null, null, false, null);
            var workerType = engine.GetNestedType("DestinationWorker", BindingFlags.NonPublic)!;
            var worker = Activator.CreateInstance(workerType,
                [path, 0, progress, device, scheduler, AdaptiveControlByteBudget.CreateForSystem()])!;
            workerType.GetMethod("ReservePendingPayload")!.Invoke(worker, [bytes]);
            scheduler.ReserveBacklog(bytes);
            var entryType = engine.GetNestedType("FileEntry", BindingFlags.NonPublic)!;
            var entry = Activator.CreateInstance(entryType,
                [path, "payload.bin", (long)bytes, DateTime.UtcNow, 0L])!;
            var currentType = engine.GetNestedType("CurrentFile", BindingFlags.NonPublic)!;
            var current = Activator.CreateInstance(currentType,
                [entry, path, path, path + ".bak", stream, null, false])!;
            var block = new CopyEngine.SharedBlock(lease, bytes);
            var task = (Task)engine.GetMethod("WriteBlockAtOffsetAsync", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [worker, current, block, 0L, job])!;
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            Assert.AreEqual("Failed", result.GetType().GetProperty("Status")!.GetValue(result)!.ToString());
            Assert.IsInstanceOfType<OperationCanceledException>(result.GetType().GetProperty("Error")!.GetValue(result));
            Assert.AreEqual(0, pool.UsedBytes);
            Assert.AreEqual(0L, scheduler.QueuedBytes);
            Assert.AreEqual(0L, workerType.GetProperty("PendingPayloadBytes")!.GetValue(worker));
            Assert.AreEqual(1, stream.Attempts);
            Assert.AreEqual(0L, stream.Length);
            pool.Dispose(); // COPY cleanup must succeed while the job remains alive.
        }
        finally
        {
            if (lease.RemainingReferences > 0) lease.Dispose();
            File.Delete(path);
        }
    }

    private sealed class CancelAndFailStream(string path, CopyJob job)
        : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    {
        public int Attempts { get; private set; }
        public override SafeFileHandle SafeFileHandle
        {
            get
            {
                Attempts++;
                job.RequestCancel();
                throw new IOException("Injected sharing violation", unchecked((int)0x80070020));
            }
        }
    }
}
