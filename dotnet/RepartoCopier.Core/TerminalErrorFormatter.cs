using System.ComponentModel;
using System.Text;

namespace RepartoCopier.Core;

public static class TerminalErrorFormatter
{
    public static string Format(
        Exception error,
        string? destination = null,
        string? file = null,
        string? phase = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        var flattened = Flatten(error);
        var native = FindNativeError(flattened);
        var leaf = FindLeaf(flattened);
        var hResult = unchecked((uint)leaf.HResult);
        var builder = new StringBuilder();

        AppendContext(builder, destination, file, phase);
        builder.AppendLine($"Tipo: {leaf.GetType().FullName}");
        builder.AppendLine($"HResult: 0x{hResult:X8} ({leaf.HResult})");
        if (native > 0)
            builder.AppendLine($"Win32 Native Error: {native}");
        builder.AppendLine($"Mensaje: {leaf.Message}");

        if (leaf.InnerException is not null)
            builder.AppendLine($"Inner: {leaf.InnerException.Message}");

        return builder.ToString().TrimEnd();
    }

    public static string FormatSnapshotError(
        string? error,
        string? destination = null,
        string? file = null,
        string? phase = null)
    {
        var builder = new StringBuilder();
        AppendContext(builder, destination, file, phase);
        builder.AppendLine($"Error: {(string.IsNullOrWhiteSpace(error) ? "sin detalle" : error)}");
        return builder.ToString().TrimEnd();
    }

    public static string FormatSnapshots(IReadOnlyList<DestinationSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var failed = snapshots
            .Where(item => item.Phase == DestinationPhase.Failed || item.FilesErrored > 0 || !string.IsNullOrWhiteSpace(item.Error))
            .ToArray();
        if (failed.Length == 0)
            return string.Empty;

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            failed.Select(item => FormatSnapshotError(
                item.Error,
                destination: item.Label,
                file: item.LastFile,
                phase: item.Phase.ToString())));
    }

    private static void AppendContext(StringBuilder builder, string? destination, string? file, string? phase)
    {
        if (!string.IsNullOrWhiteSpace(destination))
            builder.AppendLine($"Destino: {destination}");
        if (!string.IsNullOrWhiteSpace(file))
            builder.AppendLine($"Archivo: {file}");
        if (!string.IsNullOrWhiteSpace(phase))
            builder.AppendLine($"Fase: {phase}");
    }

    private static Exception Flatten(Exception error)
    {
        if (error is AggregateException aggregate)
            return aggregate.Flatten().InnerExceptions.FirstOrDefault() ?? aggregate;
        return error;
    }

    private static Exception FindLeaf(Exception error)
    {
        var current = error;
        while (current.InnerException is not null)
            current = current.InnerException;
        return current;
    }

    private static int FindNativeError(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception win32)
                return win32.NativeErrorCode;
            if (TransientIoErrorClassifier.TryGetNativeCode(current, out var native))
                return native;
        }
        return 0;
    }
}
