using RepartoCopier.Core;

namespace RepartoCopier.WinUI;

internal static class DestinationProgressText
{
    internal static string Format(DestinationSnapshot snapshot) => snapshot.Phase switch
    {
        DestinationPhase.Copying => $"Copia {Percent(snapshot.Written, snapshot.Total)}%",
        DestinationPhase.Verifying => $"Verif. {Percent(snapshot.VerifiedBytes, snapshot.VerifyBytesTotal)}%",
        DestinationPhase.Done => snapshot.FilesErrored > 0 ? "Con errores" : "Completado",
        DestinationPhase.Failed => "Fallido",
        DestinationPhase.Cancelled => "Cancelado",
        _ => "Preparando",
    };

    internal static int FloorPercent(double percent) => (int)Math.Clamp(Math.Floor(percent), 0, 100);

    private static int Percent(ulong bytes, ulong total) => total == 0
        ? 0 : FloorPercent(bytes * 100d / total);
}
