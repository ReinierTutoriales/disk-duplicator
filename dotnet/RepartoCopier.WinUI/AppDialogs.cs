using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using RepartoCopier.Core;

namespace RepartoCopier.WinUI;

/// <summary>The application's dialogs, each one a themed, owned, modal <see cref="AppDialog"/> window.</summary>
internal static class AppDialogs
{
    internal const string GitHubUrl = "https://github.com/ReinierTutoriales/disk-duplicator";
    internal const string LicenseUrl = "https://github.com/ReinierTutoriales/disk-duplicator/blob/main/LICENSE";

    internal static async Task<ThemePreference?> SettingsAsync(Window owner, ThemePreference selected,
        CancellationToken token, Action<AppDialog>? onShown = null)
    {
        var choices = new RadioButtons { Header = "Tema de la aplicación" };
        foreach (var label in new[] { "Usar el del sistema", "Claro", "Oscuro" }) choices.Items.Add(label);
        choices.SelectedIndex = selected switch { ThemePreference.Light => 1, ThemePreference.Dark => 2, _ => 0 };
        var button = await AppDialog.ShowAsync(owner, new AppDialogSpec("Ajustes", string.Empty,
            ["Aplicar", "Cancelar"], DefaultButton: 0, CancelButton: 1, AppDialogIcon.Settings, Extra: choices,
            WidthDip: 400), token, onShown);
        if (button != 0) return null;
        return choices.SelectedIndex switch { 1 => ThemePreference.Light, 2 => ThemePreference.Dark, _ => ThemePreference.System };
    }

    internal static Task AboutAsync(Window owner, string version, Action<string> openUrl, CancellationToken token,
        Action<AppDialog>? onShown = null)
    {
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(-12, 0, 0, 0) };
        links.Children.Add(Link("Ver en GitHub", GitHubUrl, openUrl));
        links.Children.Add(Link("Licencia", LicenseUrl, openUrl));
        return AppDialog.ShowAsync(owner, new AppDialogSpec("RepartoCopier",
            $"Versión {version}\n\nCopia un origen a varios destinos a la vez, cada uno a la velocidad de su disco.\n\nSi te resulta útil, apoya el proyecto con una estrella en GitHub.",
            ["Cerrar"], DefaultButton: 0, CancelButton: 0, AppDialogIcon.Logo,
            Footer: "© 2026 ReinierTutoriales. Todos los derechos reservados.", Extra: links), token, onShown);
    }

    internal static async Task<bool> ShutdownAsync(Window owner, CancellationToken token, Action<AppDialog>? onShown = null) =>
        await AppDialog.ShowAsync(owner, new AppDialogSpec("¿Apagar el equipo?",
            "La copia terminó. Si eliges Apagar, el equipo se apagará en 60 segundos. Puedes cancelarlo desde Windows con shutdown /a.",
            ["Apagar", "No apagar"], DefaultButton: 1, CancelButton: 1, AppDialogIcon.Warning), token, onShown) == 0;

    /// <summary>Every destination in one scrollable list; complete paths and messages stay in the JSON export.</summary>
    internal static async Task<bool> ResultsAsync(Window owner, IReadOnlyList<DestinationSnapshot> destinations,
        bool verifyRequested, bool canSave, CancellationToken token, Action<AppDialog>? onShown = null)
    {
        var failed = destinations.Count(item => item.Phase == DestinationPhase.Failed);
        var withErrors = destinations.Count(item => item.Phase != DestinationPhase.Failed && item.FilesErrored > 0);
        var cancelled = destinations.Any(item => item.Phase == DestinationPhase.Cancelled);
        var list = new StackPanel { Spacing = 8 };
        foreach (var item in destinations) list.Children.Add(ResultRow(item, verifyRequested));
        if (destinations.Count == 0)
            list.Children.Add(new TextBlock { Text = "No hay resultados.", TextWrapping = TextWrapping.Wrap });
        var (heading, icon) = failed > 0 || withErrors > 0
            ? ("Terminó con errores", AppDialogIcon.Error)
            : cancelled ? ("Copia cancelada", AppDialogIcon.Warning) : ("Copia completada", AppDialogIcon.Success);
        string[] buttons = canSave ? ["Guardar diagnóstico", "Cerrar"] : ["Cerrar"];
        var close = buttons.Length - 1;
        var button = await AppDialog.ShowAsync(owner, new AppDialogSpec(heading,
            $"{destinations.Count} destino{(destinations.Count == 1 ? string.Empty : "s")}", buttons,
            DefaultButton: close, CancelButton: close, icon, Extra: list, WidthDip: 520), token, onShown);
        return canSave && button == 0;
    }

    private static HyperlinkButton Link(string text, string url, Action<string> openUrl)
    {
        var link = new HyperlinkButton { Content = text };
        link.Click += (_, _) => openUrl(url);
        return link;
    }

    private static FrameworkElement ResultRow(DestinationSnapshot item, bool verifyRequested)
    {
        var (glyph, brush) = item.Phase switch
        {
            DestinationPhase.Failed => ("EB90", "SystemFillColorCriticalBrush"),
            DestinationPhase.Cancelled => ("E711", "SystemFillColorCautionBrush"),
            _ when item.FilesErrored > 0 => ("E7BA", "SystemFillColorCautionBrush"),
            _ => ("EC61", "SystemFillColorSuccessBrush"),
        };
        // Theme brushes come from XAML; user-controlled text is assigned afterwards, never parsed.
        var row = (Grid)XamlReader.Load($$"""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  Padding="12" ColumnSpacing="12" CornerRadius="{ThemeResource ControlCornerRadius}"
                  Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                  BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                <FontIcon Glyph="&#x{{glyph}};" FontSize="16" VerticalAlignment="Top" Margin="0,2,0,0"
                          Foreground="{ThemeResource {{brush}}}"/>
                <StackPanel Grid.Column="1" Spacing="2">
                    <TextBlock x:Name="Label" Style="{StaticResource BodyStrongTextBlockStyle}" TextTrimming="CharacterEllipsis"/>
                    <TextBlock x:Name="Summary" Style="{StaticResource CaptionTextBlockStyle}" TextWrapping="Wrap"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                    <TextBlock x:Name="Error" Style="{StaticResource CaptionTextBlockStyle}" TextWrapping="Wrap"
                               Foreground="{ThemeResource SystemFillColorCriticalBrush}" IsTextSelectionEnabled="True"/>
                </StackPanel>
            </Grid>
            """);
        var label = (TextBlock)row.FindName("Label");
        label.Text = item.Label;
        ToolTipService.SetToolTip(label, item.Label);
        ((TextBlock)row.FindName("Summary")).Text =
            $"{DestinationProgressText.Format(item)} · Archivos {item.FilesDone}/{item.FilesTotal} · Omitidos {item.FilesSkipped} · Errores {item.FilesErrored}\n" +
            $"Verificación final: {(verifyRequested ? DestinationProgressText.Verification(item) : "No solicitada")}";
        var error = (TextBlock)row.FindName("Error");
        error.Text = Shorten(item.Error);
        error.Visibility = string.IsNullOrWhiteSpace(item.Error) ? Visibility.Collapsed : Visibility.Visible;
        return row;
    }

    private static string Shorten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= 300 ? value : value[..299] + "…";
    }
}
