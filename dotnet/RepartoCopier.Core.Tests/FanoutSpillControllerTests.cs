using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FanoutSpillControllerTests
{
    private const long Target = 256L * 1024 * 1024;

    [TestMethod]
    public void FastDestinationNeverSpillsBelowItsBacklogTarget()
    {
        var flow = new FanoutSpillController();
        Assert.IsFalse(flow.ShouldSpill(Target - 1, Target, 0));
        Assert.AreEqual(FanoutSpillState.Normal, flow.State);
    }

    [TestMethod]
    public void PressureEntersSpillWithoutChangingDeviceQueueDepth()
    {
        var flow = new FanoutSpillController();
        using var scheduler = new DeviceScheduler("nvme-test", 8, Target);

        Assert.IsTrue(flow.ShouldSpill(Target, Target, 0));
        Assert.AreEqual(FanoutSpillState.Spill, flow.State);
        Assert.AreEqual(8, scheduler.CurrentQueueDepth);
        Assert.AreEqual(8, scheduler.ExplorationQueueDepth);
    }

    [TestMethod]
    public void SpillDoesNotOscillateAtTheEntryThreshold()
    {
        var flow = new FanoutSpillController();
        Assert.IsTrue(flow.ShouldSpill(Target, Target, 0));
        Assert.IsTrue(flow.ShouldSpill(Target - 1, Target, 8 * 1024 * 1024));
        Assert.IsTrue(flow.ShouldSpill(Target * 3 / 4, Target, 0));
        Assert.AreEqual(FanoutSpillState.Spill, flow.State);
    }

    [TestMethod]
    public void RecoveryRequiresPrivatePayloadsDrainedAndHalfBacklog()
    {
        var flow = new FanoutSpillController();
        Assert.IsTrue(flow.ShouldSpill(Target, Target, 0));
        Assert.IsTrue(flow.ShouldSpill(Target / 4, Target, 1));
        Assert.IsFalse(flow.ShouldSpill(Target / 2, Target, 0));
        Assert.AreEqual(FanoutSpillState.Normal, flow.State);
    }

    [TestMethod]
    public void FailedDestinationNeverReentersFlow()
    {
        var flow = new FanoutSpillController();
        flow.Fail();
        Assert.IsFalse(flow.ShouldSpill(Target * 2, Target, 0));
        Assert.AreEqual(FanoutSpillState.Failed, flow.State);
    }

    [TestMethod]
    public void EvaluationNeverChangesState()
    {
        var flow = new FanoutSpillController();
        Assert.IsTrue(flow.WouldSpill(Target, Target, 0));
        Assert.AreEqual(FanoutSpillState.Normal, flow.State);
        Assert.IsTrue(flow.EnterSpill());
        Assert.IsFalse(flow.WouldSpill(Target / 2, Target, 0));
        Assert.AreEqual(FanoutSpillState.Spill, flow.State);
        flow.ExitSpill();
        Assert.AreEqual(FanoutSpillState.Normal, flow.State);
    }

    [TestMethod]
    public void ExplicitTransitionsNeverReviveFailedDestination()
    {
        var flow = new FanoutSpillController();
        flow.EnterSpill();
        flow.Fail();
        flow.ExitSpill();
        Assert.IsFalse(flow.EnterSpill());
        Assert.IsFalse(flow.WouldSpill(Target, Target, 1024));
        Assert.AreEqual(FanoutSpillState.Failed, flow.State);
    }

    [TestMethod]
    public void EvaluationRejectsInvalidPressureWithoutChangingState()
    {
        var flow = new FanoutSpillController();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => flow.WouldSpill(-1, Target, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => flow.WouldSpill(0, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => flow.WouldSpill(0, Target, -1));
        Assert.AreEqual(FanoutSpillState.Normal, flow.State);
    }

}
