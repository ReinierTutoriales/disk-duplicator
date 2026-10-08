using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
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

    internal const int ShutdownCountdownSeconds = 60;

    /// <summary>
    /// "Apagar al terminar" was already chosen, so silence means yes: the dialog counts down and confirms on its
    /// own, which is what makes the option work on an unattended PC. Cancelling stays one click (and Escape).
    /// </summary>
    internal static async Task<bool> ShutdownAsync(Window owner, CancellationToken token, Action<AppDialog>? onShown = null)
    {
        var remaining = ShutdownCountdownSeconds;
        var countdown = new TextBlock
        {
            Text = CountdownText(remaining),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };
        AutomationProperties.SetLiveSetting(countdown, AutomationLiveSetting.Polite);
        AppDialog? shown = null;
        var timer = owner.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (sender, _) =>
        {
            remaining--;
            if (remaining > 0)
            {
                countdown.Text = CountdownText(remaining);
                return;
            }
            sender.Stop();
            shown?.Close(0);
        };
        try
        {
            return await AppDialog.ShowAsync(owner, new AppDialogSpec("Apagando el equipo",
                "La copia terminó correctamente y elegiste apagar al terminar.",
                ["Apagar ahora", "Cancelar apagado"], DefaultButton: 1, CancelButton: 1, AppDialogIcon.Warning,
                Extra: countdown), token, dialog =>
                {
                    shown = dialog;
                    timer.Start();
                    onShown?.Invoke(dialog);
                }) == 0;
        }
        finally
        {
            timer.Stop();
        }

        static string CountdownText(int seconds) => $"Se apagará en {seconds} s.";
    }

    /// <summary>Every destination in one scrollable list; complete paths and messages stay in the JSON export.</summary>
    internal static async Task<bool> ResultsAsync(Window owner, IReadOnlyList<DestinationSnapshot> destinations,
        bool verifyRequested, bool canSave, CancellationToken token, Action<AppDialog>? onShown = null)
    {
        var failed = destinations.Count(item => item.Phase == DestinationPhase.Failed);
        var withErrors = destinations.Count(item => item.Phase != DestinationPhase.Failed && item.FilesErrored > 0);
        var cancelled = destinations.Any(item => item.Phase == DestinationPhase.Cancelled);
        // One parsed template for every row instead of two XAML parses per destination (up to 256). Every row is
        // realized up front so the dialog can size itself to the real content height.
        UIElement list = destinations.Count == 0
            ? new TextBlock { Text = "No hay resultados.", TextWrapping = TextWrapping.Wrap }
            : new ItemsControl
            {
                ItemsSource = destinations.Select(item => new ResultRow(item, verifyRequested)).ToArray(),
                ItemTemplate = ResultRowTemplate.Value,
                IsTabStop = false,
            };
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

    // Theme brushes come from XAML; user-controlled text only arrives through bindings, never parsed.
    private static readonly Lazy<DataTemplate> ResultRowTemplate = new(() => (DataTemplate)XamlReader.Load("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <Grid Margin="0,0,0,8" Padding="12" ColumnSpacing="12" CornerRadius="{ThemeResource ControlCornerRadius}"
                  Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                  BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                <Grid VerticalAlignment="Top" Margin="0,2,0,0">
                    <FontIcon Glyph="&#xEC61;" FontSize="16" Foreground="{ThemeResource SystemFillColorSuccessBrush}"
                              Visibility="{Binding SuccessVisibility}"/>
                    <FontIcon Glyph="&#xE7BA;" FontSize="16" Foreground="{ThemeResource SystemFillColorCautionBrush}"
                              Visibility="{Binding WarningVisibility}"/>
                    <FontIcon Glyph="&#xEB90;" FontSize="16" Foreground="{ThemeResource SystemFillColorCriticalBrush}"
                              Visibility="{Binding ErrorIconVisibility}"/>
                </Grid>
                <StackPanel Grid.Column="1" Spacing="2">
                    <TextBlock Text="{Binding Label}" ToolTipService.ToolTip="{Binding Label}"
                               Style="{StaticResource BodyStrongTextBlockStyle}" TextTrimming="CharacterEllipsis"/>
                    <TextBlock Text="{Binding Summary}" Style="{StaticResource CaptionTextBlockStyle}" TextWrapping="Wrap"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                    <TextBlock Text="{Binding Error}" Visibility="{Binding ErrorVisibility}"
                               Style="{StaticResource CaptionTextBlockStyle}" TextWrapping="Wrap"
                               Foreground="{ThemeResource SystemFillColorCriticalBrush}" IsTextSelectionEnabled="True"/>
                </StackPanel>
            </Grid>
        </DataTemplate>
        """));
}

/// <summary>One destination in the results dialog, shaped for the row template's bindings.</summary>
public sealed class ResultRow
{
    internal ResultRow(DestinationSnapshot item, bool verifyRequested)
    {
        Label = item.Label;
        Summary =
            $"{DestinationProgressText.Format(item)} · Archivos {item.FilesDone}/{item.FilesTotal} · Omitidos {item.FilesSkipped} · Errores {item.FilesErrored}\n" +
            $"Verificación final: {(verifyRequested ? DestinationProgressText.Verification(item) : "No solicitada")}";
        Error = Shorten(item.Error);
        ErrorVisibility = Error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var failed = item.Phase == DestinationPhase.Failed;
        var warning = !failed && (item.Phase == DestinationPhase.Cancelled || item.FilesErrored > 0);
        ErrorIconVisibility = failed ? Visibility.Visible : Visibility.Collapsed;
        WarningVisibility = warning ? Visibility.Visible : Visibility.Collapsed;
        SuccessVisibility = !failed && !warning ? Visibility.Visible : Visibility.Collapsed;
    }

    public string Label { get; }
    public string Summary { get; }
    public string Error { get; }
    public Visibility ErrorVisibility { get; }
    public Visibility SuccessVisibility { get; }
    public Visibility WarningVisibility { get; }
    public Visibility ErrorIconVisibility { get; }

    private static string Shorten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= 300 ? value : value[..299] + "…";
    }
}
