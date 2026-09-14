using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class UnificationContractTests
{
    [TestMethod]
    public void SupersededArchitecturesStayRemoved()
    {
        var assembly = typeof(StorageIoProfile).Assembly;
        Assert.IsNull(assembly.GetType("RepartoCopier.Core.StorageDeviceProfile"));
        Assert.IsNull(assembly.GetType("RepartoCopier.Core.SessionStore"));
        Assert.IsNull(assembly.GetType("RepartoCopier.Core.DiagnosticsReport"));
        Assert.IsNull(assembly.GetType("RepartoCopier.Core.GlobalControlBacklogBudget"));
        Assert.IsNull(assembly.GetType("RepartoCopier.Core.WritePolicyDiagnosticsSnapshot"));

        var schedulerMethods = typeof(DeviceScheduler)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(schedulerMethods, "NoteQueuedBytes");
        CollectionAssert.DoesNotContain(schedulerMethods, "NoteDequeuedBytes");

        var recoveryMethods = typeof(RecoveryManager)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(recoveryMethods, "AppendDurable");

        var preallocation = typeof(StoragePreallocationPolicy).GetMethod(
            "GetPreallocationSize",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(preallocation);
        Assert.AreEqual(2, preallocation.GetParameters().Length);

        var storageMethods = typeof(AtomicStorage)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(storageMethods, "ReadRegularFile");
    }

    [TestMethod]
    public void HardPerDeviceBacklogWaiterArchitectureStaysRemoved()
    {
        var nestedTypes = typeof(DeviceScheduler)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Select(type => type.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(nestedTypes, "BacklogWaiter");

        var fields = typeof(DeviceScheduler)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => field.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(fields, "_backlogWaiters");
    }

    [TestMethod]
    public void FixedQd2WriterArchitectureStaysRemoved()
    {
        var schedulerMethods = typeof(DeviceScheduler)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(schedulerMethods, "AcquireIoPairAsync");
        CollectionAssert.DoesNotContain(schedulerMethods, "AcquireIoSlotsAsync");

        var schedulerNested = typeof(DeviceScheduler)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Select(type => type.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(schedulerNested, "IoPairLease");

        var writerMethods = typeof(ExplicitOffsetWriter)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(writerMethods, "WriteTwoAsync");

        var engineMethods = typeof(CopyEngine)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(engineMethods, "WriteQueueDepthTwoAsync");

        var policyMethods = typeof(StorageWritePolicy)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(policyMethods, "BufferedLargeWriteQueueDepth");
        CollectionAssert.Contains(policyMethods, "LargeWriteQueueDepth");

        var coordinator = typeof(DestinationWriteCoordinator).GetMethod(
            "WriteAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(coordinator);
    }

    [TestMethod]
    public void DirectDestinationWriterIsSingleProductionStrategyNotAnOrphanHelper()
    {
        var directMethods = typeof(DirectIoDestinationWriter)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.Contains(directMethods, "IsEligible");
        CollectionAssert.Contains(directMethods, "TryOpen");
        CollectionAssert.Contains(directMethods, "IsFallbackable");

        var snapshotProperties = typeof(CopyDiagnosticsSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.Contains(snapshotProperties, nameof(CopyDiagnosticsSnapshot.DirectDestinationFiles));
        CollectionAssert.Contains(snapshotProperties, nameof(CopyDiagnosticsSnapshot.DirectDestinationWriteBytes));
        CollectionAssert.Contains(snapshotProperties, nameof(CopyDiagnosticsSnapshot.DirectDestinationWriteOperations));
        CollectionAssert.Contains(snapshotProperties, nameof(CopyDiagnosticsSnapshot.DirectDestinationFallbacks));
        CollectionAssert.DoesNotContain(snapshotProperties, "WriteThroughPolicy");
        CollectionAssert.DoesNotContain(snapshotProperties, "BufferedPolicy");
        CollectionAssert.DoesNotContain(snapshotProperties, "ControlBacklogWaitTime");
        CollectionAssert.DoesNotContain(snapshotProperties, "PeakControlBacklogMessages");

        var currentFile = typeof(CopyEngine)
            .GetNestedType("CurrentFile", BindingFlags.NonPublic);
        Assert.IsNotNull(currentFile);
        var currentProperties = currentFile.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.Contains(currentProperties, "DirectSession");
        CollectionAssert.Contains(currentProperties, "DirectEnabled");
        CollectionAssert.Contains(currentProperties, "DirectRequested");
        CollectionAssert.DoesNotContain(currentProperties, "WriteThrough");
        CollectionAssert.DoesNotContain(currentProperties, "PreferDirect");
    }

    [TestMethod]
    public void ObsoleteBacklogAdmissionAndQueueWaitTelemetryStayRemoved()
    {
        var schedulerMethods = typeof(DeviceScheduler)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(schedulerMethods, "TryReserveBacklog");
        CollectionAssert.DoesNotContain(schedulerMethods, "ReserveBacklogAsync");
        CollectionAssert.Contains(schedulerMethods, "ReserveBacklog");

        var diagnosticsProperties = typeof(CopyDiagnosticsSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(diagnosticsProperties, "QueueWaitTime");
    }

    [TestMethod]
    public void AtomicCommitIsSingleReplacementPrimitive()
    {
        var engineMethods = typeof(CopyEngine)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(engineMethods, "CommitPart");

        var commitMethods = typeof(AtomicFileCommit)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.Contains(commitMethods, "Commit");
    }

    [TestMethod]
    public void FanoutControlPlaneUsesAdaptiveByteBudgetWithoutFixedMessageCap()
    {
        var fanout = typeof(CopyEngine).GetNestedType("FanoutMessage", BindingFlags.NonPublic);
        var control = typeof(CopyEngine).GetNestedType("ControlMessage", BindingFlags.NonPublic);
        var begin = typeof(CopyEngine).GetNestedType("BeginMessage", BindingFlags.NonPublic);
        var data = typeof(CopyEngine).GetNestedType("DataMessage", BindingFlags.NonPublic);
        var end = typeof(CopyEngine).GetNestedType("EndMessage", BindingFlags.NonPublic);
        var delivery = typeof(CopyEngine).GetNestedType("ControlDelivery", BindingFlags.NonPublic);
        var worker = typeof(CopyEngine).GetNestedType("DestinationWorker", BindingFlags.NonPublic);

        Assert.IsNotNull(fanout);
        Assert.IsNotNull(control);
        Assert.IsNotNull(begin);
        Assert.IsNotNull(data);
        Assert.IsNotNull(end);
        Assert.IsNotNull(delivery);
        Assert.IsNotNull(worker);
        Assert.AreEqual(fanout, control.BaseType);
        Assert.AreEqual(control, begin.BaseType);
        Assert.AreEqual(fanout, data.BaseType);
        Assert.AreEqual(control, end.BaseType);
        Assert.AreEqual(fanout, delivery.BaseType);

        var workerControlBudget = worker.GetProperty(
            "ControlBudget",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.IsNotNull(workerControlBudget);
        Assert.AreEqual(typeof(AdaptiveControlByteBudget), workerControlBudget.PropertyType);

        var deliveryFields = delivery
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();
        CollectionAssert.Contains(deliveryFields, typeof(AdaptiveControlByteBudget));

        var budgetMethods = typeof(AdaptiveControlByteBudget)
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.Contains(budgetMethods, "CreateForSystem");
        CollectionAssert.Contains(budgetMethods, "AcquireAsync");
        CollectionAssert.Contains(budgetMethods, "Release");

        var deliveryMethods = typeof(CopyEngine)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name is "DeliverAsync" or "DeliverDataAsync" or "DeliverControlAsync")
            .ToArray();
        Assert.AreEqual(3, deliveryMethods.Length);
        Assert.IsFalse(deliveryMethods
            .SelectMany(method => method.GetParameters())
            .Any(parameter => string.Equals(parameter.Name, "countsData", StringComparison.Ordinal)));

        var engineMethods = typeof(CopyEngine)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(engineMethods, "DeliverOneAsync");
        CollectionAssert.DoesNotContain(engineMethods, "ReleaseIfData");
        CollectionAssert.Contains(engineMethods, "ReleaseQueuedMessage");

        var assembly = typeof(CopyEngine).Assembly;
        Assert.IsNull(assembly.GetType("RepartoCopier.Core.GlobalControlBacklogBudget"));
        Assert.IsNull(assembly.GetType("RepartoCopier.Core.ControlBacklogCapacity"));
    }

    [TestMethod]
    public void DestinationWriterKeepsMultipleBlocksInFlightWithExplicitOffsets()
    {
        var engineMethods = typeof(CopyEngine)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(engineMethods, "WriteWithRetryAsync");
        CollectionAssert.DoesNotContain(engineMethods, "SwitchToBuffered");
        CollectionAssert.DoesNotContain(engineMethods, "ResetPartLength");
        CollectionAssert.Contains(engineMethods, "WriteBlockAtOffsetAsync");
        CollectionAssert.Contains(engineMethods, "DrainPendingWritesAsync");
        CollectionAssert.Contains(engineMethods, "PruneCompletedSuccesses");
        CollectionAssert.Contains(engineMethods, "SwitchToBufferedAfterDrain");

        var currentFile = typeof(CopyEngine).GetNestedType("CurrentFile", BindingFlags.NonPublic);
        Assert.IsNotNull(currentFile);
        var properties = currentFile
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.Contains(properties, "PendingWrites");
        CollectionAssert.Contains(properties, "ScheduledBytes");
        CollectionAssert.Contains(properties, "Copied");
        CollectionAssert.Contains(properties, "DirectFallbackRequested");

        var copied = currentFile.GetProperty("Copied", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(copied);
        Assert.IsFalse(copied.CanWrite, "Completed bytes must only move through RecordCompletedWrite.");

        var reserve = currentFile.GetMethod("ReserveWriteOffset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(reserve);
        var record = currentFile.GetMethod("RecordCompletedWrite", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(record);
    }
}
