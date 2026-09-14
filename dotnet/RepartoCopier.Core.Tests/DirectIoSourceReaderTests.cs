using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class DirectIoSourceReaderTests
{
    [TestMethod]
    public void AlignedLeaseHonorsPhysicalSectorBoundary()
    {
        using var lease = SourceBufferLease.RentAligned(32 * 1024 * 1024, 4096);

        Assert.IsTrue(lease.IsPinned);
        Assert.AreEqual(0L, lease.Pointer.ToInt64() & 4095L);
        Assert.AreEqual(32 * 1024 * 1024, lease.Memory.Length);
    }

    [TestMethod]
    public void ExactLocalSsdWithKnownSectorsIsEligible()
    {
        var device = Device("NVMe", StorageMediaKind.SolidState, 512, 4096);

        Assert.IsTrue(DirectIoSourceReader.IsEligible(device, 32 * 1024 * 1024));
        Assert.AreEqual(4096, DirectIoSourceReader.RequiredAlignment(device));
    }

    [TestMethod]
    public void NetworkHddUnknownIdentityAndMisalignedTransfersFallBack()
    {
        var network = Device("Network", StorageMediaKind.SolidState, 512, 4096) with
        {
            IsNetwork = true,
            PhysicalDeviceNumber = null,
        };
        var hdd = Device("SATA", StorageMediaKind.Rotational, 512, 4096);
        var unknownIdentity = Device("USB", StorageMediaKind.SolidState, 512, 4096) with
        {
            PhysicalDeviceNumber = null,
        };
        var ssd = Device("NVMe", StorageMediaKind.SolidState, 512, 4096);

        Assert.IsFalse(DirectIoSourceReader.IsEligible(network, 32 * 1024 * 1024));
        Assert.IsFalse(DirectIoSourceReader.IsEligible(hdd, 32 * 1024 * 1024));
        Assert.IsFalse(DirectIoSourceReader.IsEligible(unknownIdentity, 32 * 1024 * 1024));
        Assert.IsFalse(DirectIoSourceReader.IsEligible(ssd, 32 * 1024 * 1024 - 1));
    }

    [TestMethod]
    public void VerificationEligibilityAllowsExactLocalHddButRejectsUnsafeTopology()
    {
        var hdd = Device("SATA", StorageMediaKind.Rotational, 512, 4096);
        var network = Device("Network", StorageMediaKind.Rotational, 512, 4096) with
        {
            IsNetwork = true,
            PhysicalDeviceNumber = null,
        };
        var unknownIdentity = Device("USB", StorageMediaKind.Rotational, 512, 4096) with
        {
            PhysicalDeviceNumber = null,
        };

        Assert.IsTrue(DirectIoSourceReader.IsVerificationEligible(hdd, 8 * 1024 * 1024));
        Assert.IsFalse(DirectIoSourceReader.IsVerificationEligible(network, 8 * 1024 * 1024));
        Assert.IsFalse(DirectIoSourceReader.IsVerificationEligible(unknownIdentity, 8 * 1024 * 1024));
        Assert.IsFalse(DirectIoSourceReader.IsVerificationEligible(hdd, 8 * 1024 * 1024 - 1));
    }

    [TestMethod]
    public void SourceFastPathExposesOverlappedDirectIoSession()
    {
        var method = typeof(DirectIoSourceReader).GetMethod(
            "TryOpenOverlapped",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.IsNotNull(method);
        var parameters = method.GetParameters();
        Assert.AreEqual(typeof(DirectIoSourceReader.OverlappedSession).MakeByRefType(), parameters[^1].ParameterType);
    }

    [TestMethod]
    public void DirectIoReaderHasNoSynchronousSessionOrOpenEntryPoints()
    {
        var nested = typeof(DirectIoSourceReader)
            .GetNestedTypes(System.Reflection.BindingFlags.NonPublic)
            .Select(type => type.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(nested, "Session");

        var methods = typeof(DirectIoSourceReader)
            .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(methods, "TryOpen");
        CollectionAssert.DoesNotContain(methods, "TryOpenForVerification");
    }

    [TestMethod]
    public void SkipSameHashHelperKeepsMinimalThreeParameterShape()
    {
        var method = typeof(CopyEngine).GetMethod(
            "HashFileAsync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.IsNotNull(method);
        var parameters = method.GetParameters();
        Assert.AreEqual(3, parameters.Length);
        Assert.AreEqual(typeof(string), parameters[0].ParameterType);
        Assert.AreEqual(typeof(CancellationToken), parameters[1].ParameterType);
        Assert.AreEqual("ResourceGovernor", parameters[2].ParameterType.Name);
    }

    [TestMethod]
    public void BufferedLeaseKeepsExistingArrayPoolContract()
    {
        using var lease = SourceBufferLease.RentBuffered(1024 * 1024);

        Assert.IsFalse(lease.IsPinned);
        Assert.AreEqual(1024 * 1024, lease.Memory.Length);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = lease.Pointer);
    }

    [TestMethod]
    public void FallbackOnlyMasksUnsupportedDirectIoNotHardwareFaults()
    {
        foreach (var code in new[] { 1, 5, 50, 87 })
            Assert.IsTrue(DirectIoSourceReader.IsFallbackable(new DirectIoSourceReader.DirectIoReadException(code, "unsupported")));

        foreach (var code in new[] { 23, 1117 })
            Assert.IsFalse(DirectIoSourceReader.IsFallbackable(new DirectIoSourceReader.DirectIoReadException(code, "hardware fault")));
    }

    private static StorageDeviceInfo Device(
        string bus,
        StorageMediaKind media,
        uint logicalSector,
        uint physicalSector) =>
        new(
            @"C:\source",
            @"C:\",
            1,
            1,
            bus,
            media,
            false,
            logicalSector,
            physicalSector,
            true,
            null,
            false,
            "NTFS",
            DriveType.Fixed,
            false,
            true,
            true,
            0);
}
