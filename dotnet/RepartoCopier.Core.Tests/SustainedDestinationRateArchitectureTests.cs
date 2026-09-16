using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SustainedDestinationRateArchitectureTests
{
    [TestMethod]
    public void GlobalSourceAndWriteRatesUseTheSameSlidingWindowPrimitive()
    {
        var telemetryFields = typeof(CopyTelemetry)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        var telemetryFieldTypes = telemetryFields
            .Select(field => field.FieldType)
            .ToArray();
        CollectionAssert.Contains(telemetryFieldTypes, typeof(SlidingByteRateWindow));
        Assert.AreEqual(3, telemetryFieldTypes.Count(type => type == typeof(SlidingByteRateWindow)));

        var legacyTelemetryFields = telemetryFields.Select(field => field.Name).ToArray();
        CollectionAssert.DoesNotContain(legacyTelemetryFields, "_rateGate");
        CollectionAssert.DoesNotContain(legacyTelemetryFields, "_writeSamples");

        var progressFieldTypes = typeof(DestinationProgress)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();
        CollectionAssert.Contains(progressFieldTypes, typeof(SlidingByteRateWindow));
        Assert.AreEqual(1, progressFieldTypes.Count(type => type == typeof(SlidingByteRateWindow)));

        var progressMethods = typeof(DestinationProgress)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.Name == "SustainedWriteRateSnapshot")
            .ToArray();
        Assert.AreEqual(2, progressMethods.Length);

        var snapshotProperties = typeof(DestinationSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.Contains(snapshotProperties, nameof(DestinationSnapshot.SustainedWrite5sBytesPerSecond));
        CollectionAssert.Contains(snapshotProperties, nameof(DestinationSnapshot.SustainedWrite10sBytesPerSecond));
    }
}
