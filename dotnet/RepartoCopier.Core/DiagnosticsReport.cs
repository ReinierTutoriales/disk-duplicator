using System.Globalization;
using System.Text;

namespace RepartoCopier.Core;

public static class DiagnosticsReport
{
    public static string Format(
        string source,
        IReadOnlyList<DestinationSnapshot> destinations,
        CopyDiagnosticsSnapshot metrics)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("RepartoCopier diagnostics");
        sb.Append("Source: ").AppendLine(source);
        sb.Append("Destinations: ").AppendLine(destinations.Count.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine();

        foreach (var destination in destinations)
        {
            sb.Append("- ").Append(destination.Label)
                .Append(" | phase=").Append(destination.Phase)
                .Append(" | written=").Append(destination.Written.ToString(CultureInfo.InvariantCulture))
                .Append('/').Append(destination.Total.ToString(CultureInfo.InvariantCulture))
                .Append(" | filesDone=").Append(destination.FilesDone.ToString(CultureInfo.InvariantCulture))
                .Append(" | skipped=").Append(destination.FilesSkipped.ToString(CultureInfo.InvariantCulture))
                .Append(" | errors=").Append(destination.FilesErrored.ToString(CultureInfo.InvariantCulture))
                .Append(" | retries=").Append(destination.Retries.ToString(CultureInfo.InvariantCulture))
                .AppendLine();
        }

        AppendStorageTopology(sb, destinations);

        sb.AppendLine();
        AppendRate(sb, "SourceRead", metrics.SourceReadBytes, metrics.SourceReadTime, metrics.SourceReadBytesPerSecond);
        AppendRate(sb, "SourceHash", metrics.SourceHashBytes, metrics.SourceHashTime, metrics.SourceHashBytesPerSecond);
        AppendScalarRate(sb, "SourceReadWallClockRate", metrics.SourceReadWallClockBytesPerSecond);
        AppendDuration(sb, "BufferWait", metrics.BufferWaitTime);
        AppendDuration(sb, "FanoutWait", metrics.FanoutWaitTime);
        AppendDuration(sb, "QueueWait", metrics.QueueWaitTime);
        AppendDuration(sb, "ControlBacklogWait", metrics.ControlBacklogWaitTime);
        AppendRate(sb, "Write", metrics.WrittenBytes, metrics.WriteTime, metrics.WriteBytesPerSecond);
        AppendScalarRate(sb, "FanoutLogicalWriteWallClockRate", metrics.FanoutLogicalWriteWallClockBytesPerSecond);
        sb.Append("WriteOperations: ").AppendLine(metrics.WriteOperations.ToString(CultureInfo.InvariantCulture));
        sb.Append("DurableFlushes: ").AppendLine(metrics.DurableFlushes.ToString(CultureInfo.InvariantCulture));
        AppendDuration(sb, "DurableFlush", metrics.DurableFlushTime);
        sb.Append("Commits: ").AppendLine(metrics.Commits.ToString(CultureInfo.InvariantCulture));
        AppendDuration(sb, "Commit", metrics.CommitTime);
        sb.Append("RecoveryEvents: ").AppendLine(metrics.RecoveryEvents.ToString(CultureInfo.InvariantCulture));
        AppendDuration(sb, "Recovery", metrics.RecoveryTime);
        AppendRate(sb, "VerifyRead", metrics.VerifyReadBytes, metrics.VerifyReadTime, metrics.VerifyReadBytesPerSecond);
        AppendRate(sb, "VerifyHash", metrics.VerifyHashBytes, metrics.VerifyHashTime, metrics.VerifyHashBytesPerSecond);
        AppendDuration(sb, "VerifyCpuWait", metrics.VerifyCpuWaitTime);
        sb.Append("PeakControlBacklogMessages: ").AppendLine(metrics.PeakControlBacklogMessages.ToString(CultureInfo.InvariantCulture));
        sb.Append("PeakBufferedBytes: ").AppendLine(metrics.PeakBufferedBytes.ToString(CultureInfo.InvariantCulture));
        sb.Append("MaximumObservedBufferTargetBytes: ").AppendLine(metrics.MaximumObservedBufferTargetBytes.ToString(CultureInfo.InvariantCulture));
        AppendDuration(sb, "CopyPhase", metrics.CopyPhaseElapsed);
        AppendDuration(sb, "VerifyPhase", metrics.VerifyPhaseElapsed);
        AppendDuration(sb, "Elapsed", metrics.Elapsed);

        return sb.ToString();
    }

    private static void AppendStorageTopology(StringBuilder sb, IReadOnlyList<DestinationSnapshot> destinations)
    {
        if (destinations.Count == 0)
            return;

        StorageTopologySnapshot topology;
        try
        {
            topology = StorageTopology.InspectDestinations(destinations.Select(item => item.Label));
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.Append("StorageTopology: unavailable | ").AppendLine(ex.Message);
            return;
        }

        sb.AppendLine();
        sb.AppendLine("StorageTopology:");
        foreach (var device in topology.Destinations)
        {
            sb.Append("- ").Append(device.DestinationRoot)
                .Append(" | disk=").Append(device.PhysicalDeviceNumber?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
                .Append(" | partition=").Append(device.PartitionNumber?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
                .Append(" | bus=").Append(device.BusType)
                .Append(" | media=").Append(device.MediaKind)
                .Append(" | logicalSector=").Append(device.LogicalSectorBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
                .Append(" | physicalSector=").Append(device.PhysicalSectorBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
                .Append(" | removable=").Append(device.Removable?.ToString() ?? "unknown")
                .Append(" | sharedPhysicalDevice=").Append(device.SharesPhysicalDevice)
                .AppendLine();
            if (!string.IsNullOrWhiteSpace(device.ProbeError))
                sb.Append("  probe-note: ").AppendLine(device.ProbeError);
        }

        foreach (var group in topology.SharedPhysicalDevices)
        {
            sb.Append("SharedPhysicalDisk ")
                .Append(group.PhysicalDeviceNumber.ToString(CultureInfo.InvariantCulture))
                .Append(": ")
                .AppendLine(string.Join(" | ", group.DestinationRoots));
        }
    }

    private static void AppendRate(StringBuilder sb, string name, long bytes, TimeSpan elapsed, double bytesPerSecond)
    {
        sb.Append(name).Append("Bytes: ").AppendLine(bytes.ToString(CultureInfo.InvariantCulture));
        AppendDuration(sb, name + "Time", elapsed);
        AppendScalarRate(sb, name + "Rate", bytesPerSecond);
    }

    private static void AppendScalarRate(StringBuilder sb, string name, double bytesPerSecond) =>
        sb.Append(name).Append(": ")
            .Append(bytesPerSecond.ToString("0.###", CultureInfo.InvariantCulture))
            .AppendLine(" B/s");

    private static void AppendDuration(StringBuilder sb, string name, TimeSpan value) =>
        sb.Append(name).Append(": ")
            .Append(value.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture))
            .AppendLine(" ms");
}
