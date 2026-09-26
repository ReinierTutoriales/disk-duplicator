using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class StoragePreallocationPolicyTests
{
    [TestCleanup]
    public void Cleanup() =>
        Environment.SetEnvironmentVariable(StoragePreallocationPolicy.DisablePreallocationEnvironmentVariable, null);

    [TestMethod]
    public void SafePreallocationIsRestrictedToLocalNtfsAndRefs()
    {
        Assert.IsTrue(StorageTopology.SupportsSafePreallocation("NTFS", isNetwork: false));
        Assert.IsTrue(StorageTopology.SupportsSafePreallocation("ReFS", isNetwork: false));
        Assert.IsFalse(StorageTopology.SupportsSafePreallocation("exFAT", isNetwork: false));
        Assert.IsFalse(StorageTopology.SupportsSafePreallocation("FAT32", isNetwork: false));
        Assert.IsFalse(StorageTopology.SupportsSafePreallocation("NTFS", isNetwork: true));
        Assert.IsFalse(StorageTopology.SupportsSafePreallocation("Unknown", isNetwork: false));
    }

    [TestMethod]
    public void PolicyMatchesCurrentTemporaryVolumeWithoutArtificialSizeThreshold()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repartocopier-prealloc-{Guid.NewGuid():N}.part");
        var root = Path.GetPathRoot(path)!;
        var drive = new DriveInfo(root);
        var isNetwork = StorageTopology.IsNetworkDestination(path, drive.DriveType);
        var expectedAllowed = drive.IsReady &&
            StorageTopology.SupportsSafePreallocation(drive.DriveFormat, isNetwork);

        Assert.AreEqual(
            expectedAllowed ? 1024L : 0L,
            StoragePreallocationPolicy.GetPreallocationSize(path, 1024));
        Assert.AreEqual(
            expectedAllowed ? 8192L : 0L,
            StoragePreallocationPolicy.GetPreallocationSize(path, 8192));
        Assert.AreEqual(0L, StoragePreallocationPolicy.GetPreallocationSize(path, 0));
    }

    [TestMethod]
    public void DiagnosticSwitchDisablesPreallocationWithoutChangingDefaultPolicy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repartocopier-prealloc-disabled-{Guid.NewGuid():N}.part");
        Environment.SetEnvironmentVariable(StoragePreallocationPolicy.DisablePreallocationEnvironmentVariable, "1");

        Assert.AreEqual(0L, StoragePreallocationPolicy.GetPreallocationSize(path, 8192));
    }

    [TestMethod]
    public void UncPathsAreAlwaysTreatedAsNetwork()
    {
        Assert.IsTrue(StorageTopology.IsNetworkDestination(@"\\server\share\file.bin", DriveType.Unknown));
        Assert.IsTrue(StorageTopology.IsNetworkDestination(@"\\?\UNC\server\share\file.bin", DriveType.Unknown));
    }
}
