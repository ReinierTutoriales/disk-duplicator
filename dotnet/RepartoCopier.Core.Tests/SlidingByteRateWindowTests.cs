using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SlidingByteRateWindowTests
{
    [TestMethod]
    public void FiveAndTenSecondRatesUseRealSlidingSamples()
    {
        var window = new SlidingByteRateWindow();
        var origin = Stopwatch.GetTimestamp();
        var second = Stopwatch.Frequency;

        window.Record(100, origin);
        window.Record(100, origin + 2 * second);
        window.Record(100, origin + 4 * second);
        window.Record(100, origin + 6 * second);
        window.Record(100, origin + 8 * second);
        window.Record(100, origin + 10 * second);

        var snapshot = window.Snapshot(origin + 10 * second);

        Assert.AreEqual(50.0, snapshot.FiveSecondsBytesPerSecond, 0.001);
        Assert.AreEqual(50.0, snapshot.TenSecondsBytesPerSecond, 0.001);
    }

    [TestMethod]
    public void OldProgressDecaysToZeroWithoutAnEwmaTail()
    {
        var window = new SlidingByteRateWindow();
        var origin = Stopwatch.GetTimestamp();
        var second = Stopwatch.Frequency;

        window.Record(1024, origin);
        window.Record(1024, origin + second);

        var snapshot = window.Snapshot(origin + 20 * second);

        Assert.AreEqual(0.0, snapshot.FiveSecondsBytesPerSecond);
        Assert.AreEqual(0.0, snapshot.TenSecondsBytesPerSecond);
    }

    [TestMethod]
    public void DestinationSnapshotCarriesSustainedRatesSeparateFromUiEwma()
    {
        var progress = new DestinationProgress("dest", 4096);
        var origin = Stopwatch.GetTimestamp();
        var second = Stopwatch.Frequency;
        progress.SetPhase(DestinationPhase.Copying);

        progress.AddWritten(1024);
        var rates = progress.SustainedWriteRateSnapshot(origin + second);

        Assert.IsTrue(rates.FiveSecondsBytesPerSecond >= 0);
        var snapshot = progress.Snapshot(origin + second);
        Assert.IsTrue(snapshot.SustainedWrite5sBytesPerSecond >= 0);
        Assert.IsTrue(snapshot.SustainedWrite10sBytesPerSecond >= 0);
    }
}