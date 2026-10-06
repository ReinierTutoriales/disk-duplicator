using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class PreflightNamespaceAndSpaceTests
{
    [TestMethod]
    public async Task SourceInsideDestinationRecoveryStateIsRejectedBeforeCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"repartocopier-owned-source-{Guid.NewGuid():N}");
        try
        {
            var destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
            var source = StateLayout.PartPath(destination, Path.Combine(destination, "payload.bin"));
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            byte[] payload = [1, 2, 3, 4];
            File.WriteAllBytes(source, payload);
            await Assert.ThrowsAsync<IOException>(async () =>
            {
                await using var job = await CopyEngine.StartAsync(CopyPlan.Create(source, [destination], false, false));
            });
            CollectionAssert.AreEqual(payload, File.ReadAllBytes(source));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DestinationCannotOverlapAnotherDestinationRecoveryState()
    {
        var root = Path.Combine(Path.GetTempPath(), "repartocopier-namespace-test");
        var destination = Path.Combine(root, "copy");
        Assert.ThrowsExactly<IOException>(() => PreflightSafety.ValidateRecoveryPaths(
            Path.Combine(root, "source.bin"), [destination, StateLayout.StateDirectoryFor(destination)]));
        PreflightSafety.ValidateRecoveryPaths(Path.Combine(root, "source.bin"),
            [destination, Path.Combine(root, "copy2")]);
    }

    [TestMethod]
    public void SharedVolumeDemandIsSummedInsteadOfReusingTheSameFreeBytes()
    {
        const ulong gib = 1024UL * 1024 * 1024;
        var metrics = new VolumeMetrics(10 * gib, 100 * gib, 4096, "volume-a");
        var first = new DestinationSpaceRequirement("first", metrics, 6 * gib, 6 * gib);
        var second = new DestinationSpaceRequirement("second", metrics, 6 * gib, 6 * gib);
        PreflightSafety.EnsureFreeSpaceForVolumes([first]);
        Assert.ThrowsExactly<IOException>(() => PreflightSafety.EnsureFreeSpaceForVolumes([first, second]));
        PreflightSafety.EnsureFreeSpaceForVolumes([first, second with { Volume = metrics with { VolumeId = "volume-b" } }]);
    }

    [TestMethod]
    public void SharedVolumeReserveIsAppliedOnceAndSkippedFilesNeedNoReserve()
    {
        const ulong gib = 1024UL * 1024 * 1024;
        var metrics = new VolumeMetrics(10 * gib, 100 * gib, 4096, "volume-a");
        var first = new DestinationSpaceRequirement("first", metrics, 4 * gib, 4 * gib);
        var second = new DestinationSpaceRequirement("second", metrics, 5 * gib, 5 * gib);
        PreflightSafety.EnsureFreeSpaceForVolumes([first, second]); // 9 GiB + 1 GiB reserve.
        PreflightSafety.EnsureFreeSpaceForVolumes([
            new DestinationSpaceRequirement("skipped", metrics with { AvailableBytes = 0 }, 0, 0)]);
    }

    [TestMethod]
    public void WindowsVolumeMetricsIdentifySiblingDirectoriesAsOneVolume()
    {
        var root = Path.Combine(Path.GetTempPath(), $"repartocopier-volume-{Guid.NewGuid():N}");
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
            var second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
            var one = WindowsNative.GetVolumeMetrics(first);
            var two = WindowsNative.GetVolumeMetrics(second);
            Assert.AreEqual(one.VolumeId, two.VolumeId);
            Assert.IsFalse(string.IsNullOrWhiteSpace(one.VolumeId));
            Assert.IsGreaterThan(0UL, one.AllocationGranularity);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
