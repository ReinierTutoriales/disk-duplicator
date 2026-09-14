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

        var preallocationMethods = typeof(StoragePreallocationPolicy)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(preallocationMethods, "ClearCacheForTests");

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
}
