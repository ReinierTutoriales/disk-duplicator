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
        Assert.IsTrue(code.Contains("public int CurrentQueueDepth => 1;", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("public int ExplorationQueueDepth => 1;", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("fixed:extreme-style", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EveryStorageClassUsesOnePhysicalIoAtATime()
    {
        var devices = new[]
        {
            Device("USB", StorageMediaKind.SolidState, true),
            Device("SATA", StorageMediaKind.SolidState, true),
            Device("NVMe", StorageMediaKind.SolidState, true),
            Device("SATA", StorageMediaKind.Rotational, false),
        };
        foreach (var device in devices) Assert.AreEqual(1, StorageIoProfile.For(device).InitialQueueDepth);
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
