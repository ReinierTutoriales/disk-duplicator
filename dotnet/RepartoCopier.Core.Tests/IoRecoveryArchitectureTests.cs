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
    public void TransientFailuresConvergeNaturallyToQueueDepthOne()
    {
        using var scheduler = new DeviceScheduler("synthetic", 8, 64L * 1024 * 1024);
        Assert.IsTrue(scheduler.RecordTransientFailure());
        Assert.AreEqual(4, scheduler.CurrentQueueDepth);
        Assert.IsTrue(scheduler.RecordTransientFailure());
        Assert.AreEqual(2, scheduler.CurrentQueueDepth);
        Assert.IsTrue(scheduler.RecordTransientFailure());
        Assert.AreEqual(1, scheduler.CurrentQueueDepth);
        Assert.IsFalse(scheduler.RecordTransientFailure());
        Assert.AreEqual(1, scheduler.CurrentQueueDepth);
    }

    [TestMethod]
    public void DiagnosticsExposeRecoveryContext()
    {
        var telemetry = new CopyTelemetry();
        telemetry.RecordIoRecovery("verify-read", @"D:\x.bin", "direct", 121, 16, 1, 4096, false);
        var item = telemetry.Snapshot().RecentIoRecoveryEvents.Single();
        Assert.AreEqual("verify-read", item.Phase);
        Assert.AreEqual("direct", item.Mode);
        Assert.AreEqual(121, item.NativeErrorCode);
        Assert.AreEqual(16, item.QueueDepth);
        Assert.AreEqual(1, item.RetryCount);
        Assert.AreEqual(4096L, item.Offset);
        Assert.IsFalse(item.Recovered);
    }

}

