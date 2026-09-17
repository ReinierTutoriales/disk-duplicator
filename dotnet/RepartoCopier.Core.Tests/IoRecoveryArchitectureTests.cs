using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class IoRecoveryArchitectureTests
{
    [TestMethod]
    public void ManagedNonWin32IOExceptionIsNotMisclassified()
    {
        var error = new IOException("managed", unchecked((int)0x80131500));
        Assert.IsFalse(TransientIoErrorClassifier.TryGetNativeCode(error, out _));
        Assert.IsFalse(TransientIoErrorClassifier.IsTransient(error));
    }

    [TestMethod]
    public void DirectReadAndWriteUseUnifiedNativeClassification()
    {
        var write = new DirectIoDestinationWriter.DirectIoWriteException(121, "synthetic");
        var read = new DirectIoSourceReader.DirectIoReadException(1237, "synthetic");
        Assert.IsTrue(TransientIoErrorClassifier.IsTransient(write));
        Assert.IsTrue(TransientIoErrorClassifier.IsTransient(read));
        Assert.AreEqual(121, TransientIoErrorClassifier.GetNativeCodeOrZero(write));
        Assert.AreEqual(1237, TransientIoErrorClassifier.GetNativeCodeOrZero(read));
    }

    [TestMethod]
    public void FixedSchedulerDoesNotOwnAnArbitraryRetryCutoff()
    {
        var root = FindRepositoryRoot();
        var scheduler = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "DeviceScheduler.cs"));
        Assert.IsFalse(scheduler.Contains("RecordTransientFailure", StringComparison.Ordinal));
        Assert.IsFalse(scheduler.Contains("_transientFailuresSinceSuccess", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DiagnosticsExposeRecoveryContext()
    {
        var telemetry = new CopyTelemetry();
        telemetry.RecordIoRecovery("verify-read", @"D:\x.bin", "direct", 121, 1, 1, 4096, false);
        var item = telemetry.Snapshot().RecentIoRecoveryEvents.Single();
        Assert.AreEqual("verify-read", item.Phase);
        Assert.AreEqual("direct", item.Mode);
        Assert.AreEqual(121, item.NativeErrorCode);
        Assert.AreEqual(1, item.QueueDepth);
        Assert.AreEqual(1, item.RetryCount);
        Assert.AreEqual(4096L, item.Offset);
        Assert.IsFalse(item.Recovered);
    }

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
