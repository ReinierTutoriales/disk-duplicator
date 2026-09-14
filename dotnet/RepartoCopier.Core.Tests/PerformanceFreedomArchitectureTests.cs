using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class PerformanceFreedomArchitectureTests
{
    [TestMethod]
    public void FixedSmallFilePerformanceFloorsStayRemoved()
    {
        var writePolicyFields = typeof(StorageWritePolicy)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => field.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(writePolicyFields, "ParallelFileThresholdBytes");

        var directWriterFields = typeof(DirectIoDestinationWriter)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => field.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(directWriterFields, "MinimumFileSize");

        var copyEngineFields = typeof(CopyEngine)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => field.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(copyEngineFields, "SourcePrefetchThreshold");

        var queueDepth = typeof(StorageWritePolicy).GetMethod(
            nameof(StorageWritePolicy.LargeWriteQueueDepth),
            BindingFlags.Static | BindingFlags.Public);
        Assert.IsNotNull(queueDepth);
        var parameters = queueDepth.GetParameters();
        Assert.AreEqual(3, parameters.Length);
        CollectionAssert.AreEqual(
            new[] { "device", "schedulerExplorationDepth", "dataLength" },
            parameters.Select(parameter => parameter.Name).ToArray());
    }

    [TestMethod]
    public void SmallExactLocalPayloadCanUseAdaptiveParallelism()
    {
        var device = ExactLocalNvme();
        const int payload = 256 * 1024;

        var depth = StorageWritePolicy.LargeWriteQueueDepth(device, 64, payload);

        Assert.IsGreaterThan(1, depth);
        Assert.AreEqual(payload / StorageWritePolicy.MinimumParallelSliceBytes, depth);
    }

    [TestMethod]
    public void DirectWriteEligibilityHasNoArtificialSizeFloor()
    {
        var device = ExactLocalNvme();

        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(device, 1));
        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(device, 4096));
        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(device, 1024 * 1024));
    }

    [TestMethod]
    public void AlignedFanoutPayloadCanServeDirectDestinationsWithoutRestaging()
    {
        using var payload = SourceBufferLease.RentAligned(1024 * 1024, DirectIoSourceReader.MaximumSupportedAlignment);

        Assert.IsTrue(payload.IsAlignedFor(512));
        Assert.IsTrue(payload.IsAlignedFor(4096));
        Assert.IsTrue(payload.IsAlignedFor(64 * 1024));
    }

    private static StorageDeviceInfo ExactLocalNvme() =>
        new(
            @"E:\copy",
            @"E:\",
            4,
            1,
            "NVMe",
            StorageMediaKind.SolidState,
            false,
            512,
            4096,
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
