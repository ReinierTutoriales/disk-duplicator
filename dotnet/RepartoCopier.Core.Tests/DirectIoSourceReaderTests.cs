using Microsoft.Win32.SafeHandles;
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
    public void LocalSsdAndHddWithKnownSectorsAreEligible()
    {
        var ssd = Device("NVMe", StorageMediaKind.SolidState, 512, 4096);
        var hdd = Device("SATA", StorageMediaKind.Rotational, 512, 4096);

        Assert.IsTrue(DirectIoSourceReader.IsEligible(ssd, 32 * 1024 * 1024));
        Assert.IsTrue(DirectIoSourceReader.IsEligible(hdd, 32 * 1024 * 1024));
        Assert.AreEqual(4096, DirectIoSourceReader.RequiredAlignment(ssd));
        Assert.AreEqual(4096, DirectIoSourceReader.RequiredAlignment(hdd));
    }

    [TestMethod]
    public void AlignmentAboveSixtyFourKiBIsDerivedFromDeviceNotArtificiallyRejected()
    {
        const int alignment = 128 * 1024;
        var device = Device("NVMe", StorageMediaKind.SolidState, 4096, alignment);

        Assert.AreEqual(alignment, DirectIoSourceReader.RequiredAlignment(device));
        Assert.IsTrue(DirectIoSourceReader.IsEligible(device, 2 * alignment));
        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(device, alignment));
        using var lease = SourceBufferLease.RentAligned(2 * alignment, alignment);
        Assert.IsTrue(lease.IsAlignedFor(alignment));
    }
    [TestMethod]
    public void NetworkAndMisalignedTransfersFallBackButUncertainLocalIdentityCanAttemptDirectIo()
    {
        var network = Device("Network", StorageMediaKind.SolidState, 512, 4096) with
        {
            IsNetwork = true,
            PhysicalDeviceNumber = null,
        };
        var uncertainLocal = Device("USB", StorageMediaKind.SolidState, 512, 4096) with
        {
            PhysicalDeviceNumber = null,
        };
        var ssd = Device("NVMe", StorageMediaKind.SolidState, 512, 4096);

        Assert.IsFalse(DirectIoSourceReader.IsEligible(network, 32 * 1024 * 1024));
        Assert.IsTrue(DirectIoSourceReader.IsEligible(uncertainLocal, 32 * 1024 * 1024));
        Assert.IsFalse(DirectIoSourceReader.IsEligible(ssd, 32 * 1024 * 1024 - 1));
    }

    [TestMethod]
    public void VerificationEligibilityAllowsAlignedLocalDevicesWithoutIdentityGate()
    {
        var hdd = Device("SATA", StorageMediaKind.Rotational, 512, 4096);
        var network = Device("Network", StorageMediaKind.Rotational, 512, 4096) with
        {
            IsNetwork = true,
            PhysicalDeviceNumber = null,
        };
        var uncertainLocal = Device("USB", StorageMediaKind.Rotational, 512, 4096) with
        {
            PhysicalDeviceNumber = null,
        };

        Assert.IsTrue(DirectIoSourceReader.IsVerificationEligible(hdd, 8 * 1024 * 1024));
        Assert.IsFalse(DirectIoSourceReader.IsVerificationEligible(network, 8 * 1024 * 1024));
        Assert.IsTrue(DirectIoSourceReader.IsVerificationEligible(uncertainLocal, 8 * 1024 * 1024));
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
    public async Task DirectIoSessionAcceptsExactUnalignedEofBeforeAlignmentGuard()
    {
        const int alignment = 4096;
        const long length = 24L * 1024 * 1024 + 193;
        using var invalidHandle = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        using var session = new DirectIoSourceReader.OverlappedSession(invalidHandle, alignment, length);
        using var lease = SourceBufferLease.RentAligned(alignment, alignment);

        var read = await session.ReadAsync(lease, alignment, length, CancellationToken.None);

        Assert.AreEqual(0, read);
        Assert.AreEqual(length, session.Length);
    }

    [TestMethod]
    public async Task DirectIoSessionStillRejectsUnalignedNonEofOffset()
    {
        const int alignment = 4096;
        const long length = 24L * 1024 * 1024 + 193;
        using var invalidHandle = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        using var session = new DirectIoSourceReader.OverlappedSession(invalidHandle, alignment, length);
        using var lease = SourceBufferLease.RentAligned(alignment, alignment);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await session.ReadAsync(lease, alignment, alignment + 1L, CancellationToken.None));
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
        Assert.IsFalse(parameters[2].HasDefaultValue);
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
