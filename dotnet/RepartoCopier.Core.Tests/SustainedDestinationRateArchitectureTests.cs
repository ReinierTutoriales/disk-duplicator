using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SustainedDestinationRateArchitectureTests
{
    [TestMethod]
    public void GlobalAndPerDestinationRatesShareOneSlidingWindowPrimitive()
    {
        var telemetryFieldTypes = typeof(CopyTelemetry)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();
        CollectionAssert.Contains(telemetryFieldTypes, typeof(SlidingByteRateWindow));

        var progressFieldTypes = typeof(DestinationProgress)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();
        CollectionAssert.Contains(progressFieldTypes, typeof(SlidingByteRateWindow));

        var snapshotProperties = typeof(DestinationSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.Contains(snapshotProperties, nameof(DestinationSnapshot.SustainedWrite5sBytesPerSecond));
        CollectionAssert.Contains(snapshotProperties, nameof(DestinationSnapshot.SustainedWrite10sBytesPerSecond));
    }
}