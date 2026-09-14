using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class BranchPendingPayloadArchitectureTests
{
    [TestMethod]
    public void SharedBlockReleaseIsCoupledToDestinationPendingPayloadAccounting()
    {
        var worker = typeof(CopyEngine).GetNestedType("DestinationWorker", BindingFlags.NonPublic);
        var sharedBlock = typeof(CopyEngine).GetNestedType("SharedBlock", BindingFlags.NonPublic);
        var currentFile = typeof(CopyEngine).GetNestedType("CurrentFile", BindingFlags.NonPublic);
        Assert.IsNotNull(worker);
        Assert.IsNotNull(sharedBlock);
        Assert.IsNotNull(currentFile);

        var releaseBranchBlock = typeof(CopyEngine).GetMethod(
            "ReleaseBranchBlock",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(releaseBranchBlock);
        var releaseParameters = releaseBranchBlock.GetParameters();
        Assert.HasCount(2, releaseParameters);
        Assert.AreEqual(worker, releaseParameters[0].ParameterType);
        Assert.AreEqual(sharedBlock, releaseParameters[1].ParameterType);

        var releasePendingWrites = typeof(CopyEngine).GetMethod(
            "ReleasePendingWritesAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(releasePendingWrites);
        var pendingParameters = releasePendingWrites.GetParameters();
        Assert.HasCount(2, pendingParameters);
        Assert.AreEqual(worker, pendingParameters[0].ParameterType,
            "Pending-write cleanup must retain destination context so branch lag accounting is released with the SharedBlock reference.");
        Assert.AreEqual(currentFile, pendingParameters[1].ParameterType);

        var reserve = worker.GetMethod("ReservePendingPayload", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var release = worker.GetMethod("ReleasePendingPayload", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(reserve);
        Assert.IsNotNull(release);
        Assert.AreEqual(typeof(int), reserve.GetParameters().Single().ParameterType);
        Assert.AreEqual(typeof(int), release.GetParameters().Single().ParameterType);
    }
}
