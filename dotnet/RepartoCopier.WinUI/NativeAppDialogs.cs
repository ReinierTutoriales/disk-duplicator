using RepartoCopier.Core;

namespace RepartoCopier.WinUI;

internal static class NativeAppDialogs
{
    internal static ThemePreference? Settings(nint owner, ThemePreference selected, CancellationToken token,
        Action<nint>? onReady = null)
    {
        var index = selected switch { ThemePreference.Light => 1, ThemePreference.Dark => 2, _ => 0 };
        var result = NativeTaskDialog.Show(owner, new NativeDialogSpec("Ajustes", "Tema de la aplicación",
            ["Aplicar", "Cerrar"], 100, Radios: ["Sistema", "Claro", "Oscuro"], SelectedRadio: index), token, onReady);
        if (result.Button != 100) return null;
        return result.Radio switch
        {
            1000 => ThemePreference.System, 1001 => ThemePreference.Light, 1002 => ThemePreference.Dark,
            _ => null,
        };
    }

    internal static void About(nint owner, string version, Action<string> openUrl, CancellationToken token,
        Action<nint>? onReady = null)
    {
        NativeTaskDialog.Show(owner, new NativeDialogSpec("Acerca de RepartoCopier",
            $"Versión {version}\nCopias rápidas y seguras para Windows.\n\n© 2026 ReinierTutoriales\nTodos los derechos reservados.\n\nGracias por usar RepartoCopier. ¡Dale ❤️ al proyecto en GitHub!",
            ["Cerrar", "Ver en GitHub", "Licencias de terceros"], 100, Icon: NativeDialogIcon.Information), token, onReady, button =>
        {
            if (button == 101) openUrl("https://github.com/ReinierTutoriales/disk-duplicator");
            else if (button == 102) openUrl("https://github.com/ReinierTutoriales/disk-duplicator/blob/main/LICENSE");
            else return null;
            return string.Empty; // open the URL without closing About
        });
    }

    internal static bool Shutdown(nint owner, CancellationToken token, Action<nint>? onReady = null) =>
        NativeTaskDialog.Show(owner, new NativeDialogSpec("Copia completada",
            "Si eliges Apagar, el equipo se apagará en 60 segundos. Puedes cancelar el apagado desde Windows con shutdown /a.",
            ["Apagar", "No apagar"], 101, Icon: NativeDialogIcon.Warning), token, onReady).Button == 100;

    internal static bool Results(nint owner, IReadOnlyList<DestinationSnapshot> destinations, bool verifyRequested,
        bool canSave, CancellationToken token, Action<nint>? onReady = null)
    {
        var pager = new NativeResultPages(destinations, verifyRequested);
        var currentPage = 0;
        var failed = destinations.Any(item => item.Phase == DestinationPhase.Failed || item.FilesErrored > 0);
        var result = NativeTaskDialog.Show(owner, new NativeDialogSpec("Resultado por destino", pager.Page(currentPage),
            ["Guardar diagnóstico", "Anterior", "Siguiente", "Cerrar"], 103,
            Icon: failed ? NativeDialogIcon.Warning : NativeDialogIcon.Information), token, window =>
        {
            NativeTaskDialog.SendMessage(window, NativeTaskDialog.EnableButton, 100, canSave ? 1 : 0);
            NativeTaskDialog.SendMessage(window, NativeTaskDialog.EnableButton, 101, currentPage > 0 ? 1 : 0);
            NativeTaskDialog.SendMessage(window, NativeTaskDialog.EnableButton, 102, currentPage + 1 < pager.Count ? 1 : 0);
            onReady?.Invoke(window);
        }, button =>
        {
            if (button is not (101 or 102)) return null;
            currentPage = Math.Clamp(currentPage + (button == 101 ? -1 : 1), 0, pager.Count - 1);
            return pager.Page(currentPage);
        });
        return canSave && result.Button == 100;
    }
}

/// <summary>Bounded native pages. Every destination is reachable; complete paths/errors remain in the JSON.</summary>
internal sealed class NativeResultPages(IReadOnlyList<DestinationSnapshot> destinations, bool verifyRequested)
{
    private const int PageSize = 3;
    internal int Count => Math.Max(1, (destinations.Count + PageSize - 1) / PageSize);
    internal string Page(int page)
    {
        if (page < 0 || page >= Count) throw new ArgumentOutOfRangeException(nameof(page));
        var entries = destinations.Skip(page * PageSize).Take(PageSize).Select(item =>
            $"{Shorten(item.Label, 96)}\n{DestinationProgressText.Format(item)} · Archivos: {item.FilesDone}/{item.FilesTotal} · Omitidos: {item.FilesSkipped} · Errores: {item.FilesErrored}\n" +
            $"Verificación final: {(verifyRequested ? DestinationProgressText.Verification(item) : "No solicitada")}" +
            (string.IsNullOrWhiteSpace(item.Error) ? string.Empty : $"\n{Shorten(item.Error, 128)}"));
        return $"Página {page + 1} de {Count} · {destinations.Count} destinos\n\n" +
            (destinations.Count == 0 ? "No hay resultados." : string.Join("\n\n", entries)) +
            "\n\nGuardar diagnóstico conserva las rutas y mensajes completos.";
    }
    private static string Shorten(string value, int maximum)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
    }
}
