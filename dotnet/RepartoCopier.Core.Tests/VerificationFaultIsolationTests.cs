using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class VerificationFaultIsolationTests
{
    // Run the production VERIFY phase against already prepared destinations.
    // This deterministically locks a destination between COPY and VERIFY without
    // adding fault injection or timing-dependent hooks to the production engine.
    [TestMethod]
    public async Task LockedVerificationOpenFailsOnlyThatDestination()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows exclusive file sharing.");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), $"repartocopier-verify-isolation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        object? prepared = null;
        var schedulers = new List<DeviceScheduler>();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var sourceFile = Path.Combine(source, "payload.bin");
            var payload = Enumerable.Range(0, 8193).Select(index => (byte)(index % 251)).ToArray();
            File.WriteAllBytes(sourceFile, payload);
            var plan = CopyPlan.Create(source, [Path.Combine(root, "blocked"), Path.Combine(root, "healthy")], ExistingFilePolicy.ReplaceAll, false);
            prepared = typeof(CopyEngine).GetMethod("Preflight", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [plan, CancellationToken.None])!;
            var preparedType = prepared.GetType();
            var roots = (string[])preparedType.GetProperty("DestinationRoots")!.GetValue(prepared)!;
            var devices = (StorageDeviceInfo[])preparedType.GetProperty("DestinationDevices")!.GetValue(prepared)!;
            var progress = roots.Select(path => new DestinationProgress(path, (ulong)payload.Length, 1)).ToArray();
            var workerType = typeof(CopyEngine).GetNestedType("DestinationWorker", BindingFlags.NonPublic)!;
            var workers = Array.CreateInstance(workerType, roots.Length);
            for (var slot = 0; slot < roots.Length; slot++)
            {
                File.Copy(sourceFile, Path.Combine(roots[slot], "payload.bin"));
                var scheduler = new DeviceScheduler($"verify-{slot}", 1, 256L * 1024 * 1024);
                schedulers.Add(scheduler);
                var worker = Activator.CreateInstance(workerType,
                    [roots[slot], slot, progress[slot], devices[slot], scheduler, AdaptiveControlByteBudget.CreateForSystem()])!;
                ((HashSet<string>)workerType.GetProperty("CompletedFiles")!.GetValue(worker)!).Add("payload.bin");
                workers.SetValue(worker, slot);
            }

            using var locked = new FileStream(Path.Combine(roots[0], "payload.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
            await using var job = new CopyJob(progress);
            var verification = (Task)typeof(CopyEngine).GetMethod("VerifyDestinationsAsync", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [prepared, workers, progress, job, null])!;
            await verification.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(DestinationPhase.Failed, progress[0].Snapshot().Phase);
            Assert.AreEqual(DestinationPhase.Verifying, progress[1].Snapshot().Phase);
            Assert.AreEqual((ulong)payload.Length, progress[1].Snapshot().VerifiedBytes);
            Assert.AreEqual(1UL, progress[1].Snapshot().VerifyFilesDone);
            Assert.IsNull(progress[1].Snapshot().Error);
        }
        finally
        {
            foreach (var scheduler in schedulers) scheduler.Dispose();
            prepared?.GetType().GetMethod("ReleaseStateLeases", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(prepared, null);
            Directory.Delete(root, recursive: true);
        }
    }
}
