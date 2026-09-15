using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class VerificationDiagnosticsTests
{
    private const int OneMiB = 1024 * 1024;

    [TestMethod]
    public void VerificationClassifiesStorageReadWhenReadServiceIsSlower()
    {
        var telemetry = new CopyTelemetry();
        telemetry.RecordVerifyRead(OneMiB, TimeSpan.FromMilliseconds(400));
        telemetry.RecordVerifyCrc32C(OneMiB, TimeSpan.FromMilliseconds(100));

        var snapshot = telemetry.Snapshot();

        Assert.AreEqual(VerificationBottleneckKind.StorageRead, snapshot.VerificationBottleneck);
    }

    [TestMethod]
    public void VerificationClassifiesCrc32CWhenChecksumServiceIsSlower()
    {
        var telemetry = new CopyTelemetry();
        telemetry.RecordVerifyRead(OneMiB, TimeSpan.FromMilliseconds(100));
        telemetry.RecordVerifyCrc32C(OneMiB, TimeSpan.FromMilliseconds(400));

        Assert.AreEqual(VerificationBottleneckKind.Crc32C, telemetry.Snapshot().VerificationBottleneck);
    }

    [TestMethod]
    public void VerificationClassifiesBalancedInsideMaterialDifferenceBand()
    {
        var telemetry = new CopyTelemetry();
        telemetry.RecordVerifyRead(OneMiB, TimeSpan.FromMilliseconds(100));
        telemetry.RecordVerifyCrc32C(OneMiB, TimeSpan.FromMilliseconds(110));

        Assert.AreEqual(VerificationBottleneckKind.Balanced, telemetry.Snapshot().VerificationBottleneck);
    }

    [TestMethod]
    public void VerificationHasNoBottleneckClassificationWithoutBothSamples()
    {
        var telemetry = new CopyTelemetry();
        telemetry.RecordVerifyRead(OneMiB, TimeSpan.FromMilliseconds(100));

        Assert.AreEqual(VerificationBottleneckKind.None, telemetry.Snapshot().VerificationBottleneck);
    }
}
