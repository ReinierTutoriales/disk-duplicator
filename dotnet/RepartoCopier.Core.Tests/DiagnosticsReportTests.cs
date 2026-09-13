using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class DiagnosticsReportTests
{
    [TestMethod]
    public void FormatIncludesThePerformanceSignalsNeededForPhysicalDiagnosis()
    {
        var destinations = new[]
        {
            new DestinationSnapshot(
                "F:\\W10UI", 1024, 2048, 3, 1, 0,
                128 * 1024 * 1024, 128 * 1024 * 1024,
                DestinationPhase.Done, null, "payload.iso", 0, 2),
        };
        var metrics = new CopyDiagnosticsSnapshot(
            1_000_000, TimeSpan.FromSeconds(1),
            1_000_000, TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(40),
            4_000_000, 2, TimeSpan.FromSeconds(2),
            2, TimeSpan.FromMilliseconds(50),
            3, TimeSpan.FromMilliseconds(60),
            4, TimeSpan.FromMilliseconds(70),
            4_000_000, TimeSpan.FromSeconds(1),
            4_000_000, TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(80), 12,
            256L * 1024 * 1024, 512L * 1024 * 1024,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4));

        var report = DiagnosticsReport.Format("E:\\W10UI", destinations, metrics);

        StringAssert.Contains(report, "SourceReadRate: 1000000 B/s");
        StringAssert.Contains(report, "BufferWait: 10 ms");
        StringAssert.Contains(report, "FanoutWait: 20 ms");
        StringAssert.Contains(report, "QueueWait: 30 ms");
        StringAssert.Contains(report, "ControlBacklogWait: 40 ms");
        StringAssert.Contains(report, "WriteRate: 2000000 B/s");
        StringAssert.Contains(report, "WriteOperations: 2");
        StringAssert.Contains(report, "DurableFlush: 50 ms");
        StringAssert.Contains(report, "VerifyReadRate: 4000000 B/s");
        StringAssert.Contains(report, "VerifyHashRate: 8000000 B/s");
        StringAssert.Contains(report, "PeakBufferedBytes: 268435456");
        StringAssert.Contains(report, "MaximumObservedBufferTargetBytes: 536870912");
        StringAssert.Contains(report, "phase=Done");
    }
}
