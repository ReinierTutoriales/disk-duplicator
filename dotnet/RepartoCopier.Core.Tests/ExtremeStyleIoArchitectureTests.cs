using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class ExtremeStyleIoArchitectureTests
{
    [TestMethod]
    public void SchedulerContainsNoAdaptiveQueueDepthExploration()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "DeviceScheduler.cs"));
        Assert.IsFalse(code.Contains("UpshiftLocked", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("DownshiftToBestLocked", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("_bestThroughputBytesPerSecond", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("_samplePeakObservedConcurrency", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("public int CurrentQueueDepth => InitialQueueDepth;", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("public int ExplorationQueueDepth => InitialQueueDepth;", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("fixed:storage-profile", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EveryStorageClassUsesItsFixedHardwareQueueDepth()
    {
        var devices = new[]
        {
            Device("USB", StorageMediaKind.SolidState, true),
            Device("SATA", StorageMediaKind.SolidState, true),
            Device("NVMe", StorageMediaKind.SolidState, true),
            Device("SATA", StorageMediaKind.Rotational, false),
        };
        CollectionAssert.AreEqual(
            new[] { 4, 4, 8, 2 },
            devices.Select(device => StorageIoProfile.For(device).InitialQueueDepth).ToArray());
    }

    [TestMethod]
    public void FixedFlowKeepsLargeSharedFanoutPoolAsThePipelineWindow()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("SharedFanoutPoolBytes = 256L * 1024 * 1024", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("SharedFanoutBlockBytes = 8 * 1024 * 1024", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("worker.Channel.Writer.TryWrite(message)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DestinationPathUsesExplicitOffsetWritesWithOverlappedAsyncQueueDepth()
    {
        var root = FindRepositoryRoot();
        var direct = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "DirectIoDestinationWriter.cs"));
        var coordinator = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "DestinationWriteCoordinator.cs"));
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(direct.Contains("FileFlagNoBuffering | FileFlagSequentialScan | FileFlagOverlapped", StringComparison.Ordinal));
        Assert.IsTrue(direct.Contains("RandomAccess.WriteAsync(handle, data, offset, token)", StringComparison.Ordinal));
        Assert.IsFalse(direct.Contains("RandomAccess.Write(handle", StringComparison.Ordinal));
        Assert.IsFalse(direct.Contains("SetFilePointerEx(handle", StringComparison.Ordinal));
        Assert.IsTrue(coordinator.Contains("RandomAccess.WriteAsync(handle, data, offset, token)", StringComparison.Ordinal));
        Assert.IsFalse(coordinator.Contains("RandomAccess.Write(handle", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("var options = FileOptions.SequentialScan;", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DirectDestinationEligibilityKeepsExtremeStyleSmallFileRule()
    {
        var root = FindRepositoryRoot();
        var direct = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "DirectIoDestinationWriter.cs"));
        Assert.IsTrue(direct.Contains("fileSize >= 64L * 1024 || fileSize % alignment == 0", StringComparison.Ordinal));
        Assert.IsTrue(direct.Contains("FileFlagNoBuffering | FileFlagSequentialScan | FileFlagOverlapped", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RecoverableIoHasNoFixedThreeFailureCutoffAndVerifyRetryIsIterative()
    {
        var root = FindRepositoryRoot();
        var scheduler = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "DeviceScheduler.cs"));
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsFalse(scheduler.Contains("RecordTransientFailure", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("RecordTransientFailure", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("DelayTransientRetryAsync", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("return await ReadVerifyTargetAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TemporaryMigrationScaffoldingIsAbsentFromFinalTree()
    {
        var root = FindRepositoryRoot();
        Assert.IsFalse(File.Exists(Path.Combine(root, "tools", "ui_compact_final_migration.py")));
        Assert.IsFalse(File.Exists(Path.Combine(root, ".github", "workflows", "ui-compact-final-migration.yml")));
    }

    private static StorageDeviceInfo Device(string bus, StorageMediaKind media, bool? trim) =>
        new(@"E:\copy", @"E:\", 4, 1, bus, media, false, 512, 4096, true, null, false, "NTFS", DriveType.Fixed, false, true, trim, 0);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new AssertFailedException("No se encontró la raíz del repositorio.");
    }
}
