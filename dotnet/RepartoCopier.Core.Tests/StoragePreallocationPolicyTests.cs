using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class StoragePreallocationPolicyTests
{
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
    public void PolicyMatchesCurrentTemporaryVolumeAndHonorsThreshold()
    {
        StoragePreallocationPolicy.ClearCacheForTests();
        var path = Path.Combine(Path.GetTempPath(), $"repartocopier-prealloc-{Guid.NewGuid():N}.part");
        var root = Path.GetPathRoot(path)!;
        var drive = new DriveInfo(root);
        var isNetwork = StorageTopology.IsNetworkDestination(path, drive.DriveType);
        var expectedAllowed = drive.IsReady &&
            StorageTopology.SupportsSafePreallocation(drive.DriveFormat, isNetwork);

        Assert.AreEqual(0L, StoragePreallocationPolicy.GetPreallocationSize(path, 1024, 4096));
        Assert.AreEqual(
            expectedAllowed ? 8192L : 0L,
            StoragePreallocationPolicy.GetPreallocationSize(path, 8192, 4096));
    }

    [TestMethod]
    public void UncPathsAreAlwaysTreatedAsNetwork()
    {
        Assert.IsTrue(StorageTopology.IsNetworkDestination(@"\\server\share\file.bin", DriveType.Unknown));
        Assert.IsTrue(StorageTopology.IsNetworkDestination(@"\\?\UNC\server\share\file.bin", DriveType.Unknown));
    }
}
