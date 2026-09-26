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
    public void SingleDestinationUnderPressureBackpressuresInsteadOfSpilling()
    {
        var flow = new FanoutSpillController();

        var shouldSpill = flow.ShouldSpill(Target, Target, 0);

        Assert.IsFalse(
            shouldSpill,
            "Un destino único no tiene pares rápidos que proteger; debe aplicar backpressure al productor en vez de entrar en spill privado y agotar un techo fijo.");
        Assert.AreEqual(FanoutSpillState.Normal, flow.State);
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
}
