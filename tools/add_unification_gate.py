from pathlib import Path

path = Path('dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs')
if path.exists():
    raise RuntimeError('UnificationContractTests.cs already exists')
path.write_text(r'''using System.Reflection;
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
    }
}
''', encoding='utf-8', newline='\n')
print('Permanent unification contract test created')
