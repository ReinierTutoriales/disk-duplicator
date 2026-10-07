using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;
using RepartoCopier.WinUI;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class WindowLayoutTests
{
    [TestMethod]
    [DataRow(96u, 540)]
    [DataRow(120u, 675)]
    [DataRow(144u, 810)]
    [DataRow(192u, 1080)]
    public void MinimumAndClientScaleWithDpi(uint dpi, int expected)
    {
        Assert.AreEqual(expected, WindowGeometry.Pixels(540, dpi));
        Assert.AreEqual(expected + 16, WindowGeometry.Minimum(dpi, 4000, 3000, 16, 39).Width);
    }

    [TestMethod]
    public void AHighDpiSmallMonitorNeverGetsAnImpossibleWindowMinimum()
    {
        var client = WindowGeometry.Client(720, 320, 192, 1024, 600, 16, 39);
        var minimum = WindowGeometry.Minimum(192, 1024, 600, 16, 39);
        Assert.IsTrue(client.Width + 16 <= 1024);
        Assert.IsTrue(client.Height + 39 <= 600);
        Assert.AreEqual(1024, minimum.Width);
        Assert.AreEqual(600, minimum.Height);
    }

    [TestMethod]
    public void EveryResultRemainsReachableAndCompleteDetailsAreNotInjectedIntoUnboundedNativeContent()
    {
        var rows = Enumerable.Range(0, 256).Select(index => new DestinationSnapshot(
            $"destination-{index}-" + new string('x', 1000), 100, 100, 10, 0, 0, 0, 0,
            DestinationPhase.Failed, new string('e', 10000), "", 0, 0)).ToArray();
        var pages = new NativeResultPages(rows, false);
        var all = string.Concat(Enumerable.Range(0, pages.Count).Select(pages.Page));
        for (var index = 0; index < rows.Length; index++) StringAssert.Contains(all, $"destination-{index}-");
        Assert.IsFalse(all.Contains(new string('e', 10000), StringComparison.Ordinal));
        Assert.IsTrue(Enumerable.Range(0, pages.Count).All(i => pages.Page(i).Length < 2000));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => pages.Page(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => pages.Page(pages.Count));
    }
}
