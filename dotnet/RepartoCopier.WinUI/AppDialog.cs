using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;
using Windows.Graphics;

namespace RepartoCopier.WinUI;

internal enum AppDialogIcon { None, Logo, Info, Warning, Error, Success, Settings }

internal sealed record AppDialogSpec(
    string Heading,
    string Body,
    string[] Buttons,
    int DefaultButton,
    int CancelButton,
    AppDialogIcon Icon = AppDialogIcon.None,
    string? Footer = null,
    UIElement? Extra = null,
    double WidthDip = 460);

/// <summary>
/// A real, separate WinUI window used as a modal dialog. It is owned by the main window and disables it while
/// open, sizes itself to its content (scrolling only past 85% of the work area) and follows the app theme:
/// every brush is a {ThemeResource}, so Light/Dark render like a Windows 11 ContentDialog. Win32 TaskDialog
/// has no dark mode, which is why it is not used.
/// </summary>
internal sealed class AppDialog
{
    internal const int Dismissed = -1;
    private const double TitleBarDip = 32;

    private readonly Window _window = new();
    private readonly nint _owner;
    private readonly TaskCompletionSource<int> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _cancelButton;
    private ElementTheme _theme;
    private readonly Func<int, bool>? _keepOpen;
    private bool _closing;

    private AppDialog(nint owner, int cancelButton, Func<int, bool>? keepOpen)
    {
        _owner = owner;
        _cancelButton = cancelButton;
        _keepOpen = keepOpen;
    }

    internal FrameworkElement Root { get; private set; } = null!;
    internal AppWindow AppWindow => _window.AppWindow;
    internal nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(_window);
    internal nint Owner => _owner;

    /// <summary>Shows the dialog and returns the clicked button index, or the cancel index when dismissed.</summary>
    internal static Task<int> ShowAsync(Window owner, AppDialogSpec spec, CancellationToken token,
        Action<AppDialog>? onShown = null, Func<int, bool>? keepOpen = null)
    {
        if (token.IsCancellationRequested) return Task.FromResult(spec.CancelButton);
        var ownerHandle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        var dialog = new AppDialog(ownerHandle, spec.CancelButton, keepOpen);
        var theme = (owner.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
        dialog.Build(spec, theme);
        dialog.Open(owner, spec.WidthDip, onShown);
        var registration = token.Register(() => dialog._window.DispatcherQueue.TryEnqueue(() => dialog.Close(spec.CancelButton)));
        _ = dialog._result.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        return dialog._result.Task;
    }

    /// <summary>Closes the dialog with a result. Safe to call more than once.</summary>
    internal void Close(int result)
    {
        if (_closing) return;
        _closing = true;
        // Re-enable the owner before the owned window goes away so Windows returns activation to it.
        EnableWindow(_owner, true);
        _result.TrySetResult(result);
        _window.Close();
    }

    private void Build(AppDialogSpec spec, ElementTheme theme)
    {
        // Built from XAML text so every brush and style is a theme resource; no user text is placed in it.
        var root = (Grid)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  Background="{ThemeResource SolidBackgroundFillColorBaseBrush}">
                <Grid.RowDefinitions>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>
                <Grid Background="{ThemeResource LayerFillColorAltBrush}">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="32"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>
                    <StackPanel x:Name="CaptionBar" Orientation="Horizontal" Spacing="8" Padding="12,0,0,0" VerticalAlignment="Stretch"
                                Background="Transparent">
                        <Image Width="16" Height="16" VerticalAlignment="Center" Source="ms-appx:///Assets/AppLogo.png"/>
                        <TextBlock Text="RepartoCopier" Style="{StaticResource CaptionTextBlockStyle}" VerticalAlignment="Center"/>
                    </StackPanel>
                    <ScrollViewer x:Name="BodyScroll" Grid.Row="1" VerticalScrollBarVisibility="Auto"
                                  HorizontalScrollBarVisibility="Disabled" HorizontalScrollMode="Disabled">
                        <Grid Padding="24,8,24,24" ColumnSpacing="16">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto"/>
                                <ColumnDefinition Width="*"/>
                            </Grid.ColumnDefinitions>
                            <Grid x:Name="IconHost" VerticalAlignment="Top" Margin="0,2,0,0"/>
                            <StackPanel Grid.Column="1" Spacing="12">
                                <TextBlock x:Name="HeadingText" Style="{StaticResource SubtitleTextBlockStyle}" TextWrapping="Wrap"/>
                                <TextBlock x:Name="BodyText" Style="{StaticResource BodyTextBlockStyle}" TextWrapping="Wrap"
                                           IsTextSelectionEnabled="True"/>
                                <ContentPresenter x:Name="ExtraHost" HorizontalContentAlignment="Stretch"/>
                                <TextBlock x:Name="FooterText" Style="{StaticResource CaptionTextBlockStyle}" TextWrapping="Wrap"
                                           Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                            </StackPanel>
                        </Grid>
                    </ScrollViewer>
                </Grid>
                <Border Grid.Row="1" Padding="24" BorderThickness="0,1,0,0"
                        BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}">
                    <Grid x:Name="ButtonGrid" ColumnSpacing="8"/>
                </Border>
            </Grid>
            """);
        root.RequestedTheme = _theme = theme;
        ((TextBlock)root.FindName("HeadingText")).Text = spec.Heading;
        var body = (TextBlock)root.FindName("BodyText");
        body.Text = spec.Body;
        body.Visibility = string.IsNullOrEmpty(spec.Body) ? Visibility.Collapsed : Visibility.Visible;
        var footer = (TextBlock)root.FindName("FooterText");
        footer.Text = spec.Footer ?? string.Empty;
        footer.Visibility = spec.Footer is null ? Visibility.Collapsed : Visibility.Visible;
        var extra = (ContentPresenter)root.FindName("ExtraHost");
        extra.Content = spec.Extra;
        extra.Visibility = spec.Extra is null ? Visibility.Collapsed : Visibility.Visible;
        var icon = CreateIcon(spec.Icon);
        if (icon is not null) ((Grid)root.FindName("IconHost")).Children.Add(icon);

        // Equal-width buttons, default one in accent, as in a Windows 11 ContentDialog command area.
        var buttons = (Grid)root.FindName("ButtonGrid");
        Button? defaultButton = null;
        for (var index = 0; index < spec.Buttons.Length; index++)
        {
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var button = new Button
            {
                Content = spec.Buttons[index],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 32,
            };
            if (index == spec.DefaultButton)
            {
                button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                defaultButton = button;
            }
            Grid.SetColumn(button, index);
            var result = index;
            button.Click += (_, _) => OnButton(result);
            buttons.Children.Add(button);
        }

        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) => { args.Handled = true; Close(_cancelButton); };
        root.KeyboardAccelerators.Add(escape);
        root.Loaded += (_, _) => defaultButton?.Focus(FocusState.Programmatic);
        Root = root;
        _window.Content = root;
    }

    private void Open(Window owner, double widthDip, Action<AppDialog>? onShown)
    {
        _window.Title = "RepartoCopier";
        _window.ExtendsContentIntoTitleBar = true;
        _window.SetTitleBar((UIElement)Root.FindName("CaptionBar"));
        var appWindow = _window.AppWindow;
        try { appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogo.ico")); } catch { }
        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        appWindow.SetPresenter(presenter);
        StyleCaptionButtons(appWindow, _theme == ElementTheme.Dark);

        // Owned + owner disabled = modal, and it stays above the main window and outside its bounds.
        SetWindowLongPtr(Handle, GwlpHwndParent, _owner);
        appWindow.Closing += (_, args) =>
        {
            if (_closing) return;
            // The caption close button means "cancel"; close outside the Closing callback.
            args.Cancel = true;
            _window.DispatcherQueue.TryEnqueue(() => Close(_cancelButton));
        };
        _window.Closed += (_, _) =>
        {
            EnableWindow(_owner, true);
            _result.TrySetResult(_cancelButton);
        };

        var dpi = Math.Max(96u, GetDpiForWindow(_owner));
        var area = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min(WindowGeometry.Pixels(widthDip, dpi), area.Width);
        // Provisional size; the real height is measured once the content is laid out (before first paint).
        Place(appWindow, owner.AppWindow, area, width, Math.Min(WindowGeometry.Pixels(240, dpi), area.Height));
        Root.Loaded += (_, _) =>
        {
            Root.Measure(new Size(widthDip, double.PositiveInfinity));
            var height = Math.Min(WindowGeometry.Pixels(Root.DesiredSize.Height, dpi), (int)(area.Height * 0.85));
            Place(appWindow, owner.AppWindow, area, width, height);
            onShown?.Invoke(this);
        };
        EnableWindow(_owner, false);
        _window.Activate();
    }

    private void OnButton(int index)
    {
        if (_keepOpen?.Invoke(index) == true) return;
        Close(index);
    }

    private static void Place(AppWindow dialog, AppWindow owner, RectInt32 area, int width, int height)
    {
        dialog.ResizeClient(new SizeInt32(width, height));
        var size = dialog.Size;
        var x = owner.Position.X + (owner.Size.Width - size.Width) / 2;
        var y = owner.Position.Y + (owner.Size.Height - size.Height) / 2;
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - size.Width));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - size.Height));
        dialog.Move(new PointInt32(x, y));
    }

    private static void StyleCaptionButtons(AppWindow window, bool dark)
    {
        try
        {
            var bar = window.TitleBar;
            var foreground = dark ? Colors.White : Colors.Black;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bar.ButtonForegroundColor = foreground;
            bar.ButtonHoverForegroundColor = foreground;
            bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(24, 128, 128, 128);
            bar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 128, 128, 128);
        }
        catch { }
    }

    private static FrameworkElement? CreateIcon(AppDialogIcon icon)
    {
        if (icon == AppDialogIcon.None) return null;
        if (icon == AppDialogIcon.Logo)
            return (FrameworkElement)XamlReader.Load("""
                <Image xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                       Width="48" Height="48" Source="ms-appx:///Assets/AppLogo.png"/>
                """);
        var (glyph, brush) = icon switch
        {
            AppDialogIcon.Warning => ("E7BA", "SystemFillColorCautionBrush"),
            AppDialogIcon.Error => ("EB90", "SystemFillColorCriticalBrush"),
            AppDialogIcon.Success => ("EC61", "SystemFillColorSuccessBrush"),
            AppDialogIcon.Settings => ("E713", "AccentTextFillColorPrimaryBrush"),
            _ => ("E946", "AccentTextFillColorPrimaryBrush"),
        };
        return (FrameworkElement)XamlReader.Load($$"""
            <FontIcon xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      Glyph="&#x{{glyph}};" FontSize="32" Foreground="{ThemeResource {{brush}}}"/>
            """);
    }

    private const int GwlpHwndParent = -8;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);
}
