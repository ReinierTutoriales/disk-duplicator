using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;
using RepartoCopier.WinUI;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class DestinationProgressTextTests
{
    private static DestinationSnapshot Snapshot(ulong written, ulong total, DestinationPhase phase) =>
        new("D:\\Backup\\Source", written, total, 0, 0, 0, 0, 0, phase, null, "", 0, 0);

    [TestMethod]
    public void CopyUsesEachDestinationsOwnBytesWithoutRoundingUpToCompletion()
    {
        Assert.AreEqual("Copia 48%", DestinationProgressText.Format(Snapshot(48, 100, DestinationPhase.Copying)));
        Assert.AreEqual("Copia 11%", DestinationProgressText.Format(Snapshot(11, 100, DestinationPhase.Copying)));
        Assert.AreEqual("Copia 99%", DestinationProgressText.Format(Snapshot(999, 1000, DestinationPhase.Copying)));
        Assert.AreEqual("Copia 100%", DestinationProgressText.Format(Snapshot(100, 100, DestinationPhase.Copying)));
    }

    [TestMethod]
    public void VerificationUsesVerifiedBytesRatherThanAlreadyCopiedBytes()
    {
        var snapshot = Snapshot(100, 100, DestinationPhase.Verifying) with
        {
            VerifiedBytes = 23,
            VerifyBytesTotal = 100,
        };
        Assert.AreEqual("Verif. 23%", DestinationProgressText.Format(snapshot));
    }

    [TestMethod]
    public void TerminalStatesReplaceThePercentageAndPreserveFileErrors()
    {
        Assert.AreEqual("Fallido", DestinationProgressText.Format(Snapshot(48, 100, DestinationPhase.Failed)));
        Assert.AreEqual("Cancelado", DestinationProgressText.Format(Snapshot(48, 100, DestinationPhase.Cancelled)));
        Assert.AreEqual("Completado", DestinationProgressText.Format(Snapshot(100, 100, DestinationPhase.Done)));
        Assert.AreEqual("Con errores", DestinationProgressText.Format(Snapshot(100, 100, DestinationPhase.Done) with { FilesErrored = 1 }));
    }

    [TestMethod]
    public void EmptyAndOutOfRangeCountersDoNotProduceInvalidPercentages()
    {
        Assert.AreEqual("Copia 0%", DestinationProgressText.Format(Snapshot(0, 0, DestinationPhase.Copying)));
        Assert.AreEqual("Copia 100%", DestinationProgressText.Format(Snapshot(200, 100, DestinationPhase.Copying)));
        Assert.AreEqual("Copia 100%", DestinationProgressText.Format(Snapshot(ulong.MaxValue, ulong.MaxValue, DestinationPhase.Copying)));
    }
}
