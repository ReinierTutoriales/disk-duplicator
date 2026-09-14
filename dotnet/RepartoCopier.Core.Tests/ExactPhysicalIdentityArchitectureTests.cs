using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class ExactPhysicalIdentityArchitectureTests
{
    [TestMethod]
    public void PhysicalCollisionAndProvenIndependenceUseTheCentralExactIdentityContract()
    {
        var identityMethods = typeof(StorageDeviceIdentity)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();

        CollectionAssert.Contains(identityMethods, "TryGetExactPhysicalDeviceNumber");
        CollectionAssert.Contains(identityMethods, "SamePhysicalDevice");
        CollectionAssert.Contains(identityMethods, "ProvenDifferentPhysicalDevices");

        var schedulerMethod = typeof(DeviceSchedulerMap).GetMethod(
            "SharesPhysicalDevice",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(schedulerMethod);
        Assert.AreEqual(typeof(bool), schedulerMethod.ReturnType);
        Assert.AreEqual(2, schedulerMethod.GetParameters().Length);
    }
}
