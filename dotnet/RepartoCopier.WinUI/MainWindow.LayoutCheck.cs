using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using RepartoCopier.Core;
using Windows.Foundation;
using Windows.Graphics;

namespace RepartoCopier.WinUI;

public sealed partial class MainWindow
{
    internal async Task RunLayoutCheckAsync(string reportPath)
    {
        var report = new List<string>();
        var capture = Environment.GetEnvironmentVariable("REPARTOCOPIER_LAYOUT_CAPTURE") == "1";
        var captureDirectory = Path.GetFullPath(reportPath) + ".images";
        if (capture) Directory.CreateDirectory(captureDirectory);
        var loaded = new TaskCompletionSource();
        if (Root.IsLoaded) loaded.SetResult();
        else Root.Loaded += (_, _) => loaded.TrySetResult();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var nativeOwner = WinRT.Interop.WindowNative.GetWindowHandle(this);
        report.Add($"Actual UI DPI: {GetDpiForWindow(nativeOwner)}. Other scale factors have geometry unit tests; not simulated render claims.");
        SourcePathBox.Text = @"H:\Una carpeta con nombres largos\ISOS";
        foreach (var drive in new[] { "D", "F", "G", "I", "J" }) _destinations.Add(new DestinationRow($"{drive}:\\ISOS"));
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        foreach (var size in new[] { new SizeInt32(540, 320), new SizeInt32(639, 320), new SizeInt32(640, 320),
            new SizeInt32(641, 320), new SizeInt32(760, 540), new SizeInt32(1200, 720) })
        {
            Root.RequestedTheme = theme;
            ResizeForCurrentDpi(size);
            ShowPreparationView();
            await SettleLayoutAsync();
            var expectedShutdownRow = Root.ActualWidth >= WideLayoutMinWidth ? 0 : 1;
            if (Grid.GetRow(ShutdownCheck) != expectedShutdownRow)
                throw new InvalidOperationException("Shutdown action did not follow the responsive layout.");
            if (Grid.GetRow(StartButton) != expectedShutdownRow)
                throw new InvalidOperationException("Start action did not follow the responsive layout.");
            foreach (var control in new FrameworkElement[] { StartButton, PickSourceFileButton, PickSourceFolderButton,
                AddDestinationsButton, ClearDestinationsButton, ExistingFilesPolicyText }) await CheckReachableAsync(control);
            OptionsExpander.IsExpanded = true;
            await SettleLayoutAsync();
            await CheckReachableAsync(VerifyCheck);
            await CheckReachableAsync(KeepGoingCheck);
            OptionsExpander.IsExpanded = false;
            if (capture)
            {
                MainContentScroll.ChangeView(null, 0, null, true);
                await SettleLayoutAsync();
                await CaptureLayoutAsync(Root, Path.Combine(captureDirectory, $"Preparation-{theme}-{size.Width}.png"));
            }
            report.Add($"Preparation {theme} {size.Width}×{size.Height} DIP: actions and expanded options reachable; labels fit.");
            ShowRunningView();
            RunningDestinationScroll.Visibility = Visibility.Visible;
            foreach (var drive in new[] { "D", "F", "G", "I", "J" })
                if (_runningDestinations.Count < 5) _runningDestinations.Add(new RunningDestinationRow($"{drive}:\\ISOS"));
            CurrentFileText.Text = new string('x', 220) + ".iso";
            CurrentPathText.Text = @"H:\Una carpeta con nombres largos\ISOS";
            OverallDetailText.Text = "Comparación: 112,95 GiB de 112,95 GiB · lectura conjunta";
            SpeedMetricText.Text = "2400,00 MiB/s";
            RemainingMetricText.Text = "01:23:45";
            FilesMetricText.Text = "999999/999999";
            NewCopyButton.Visibility = ResultDetailsButton.Visibility = Visibility.Visible;
            await SettleLayoutAsync();
            foreach (var control in new FrameworkElement[] { ShutdownCheck, PauseButton, CancelButton, NewCopyButton,
                ResultDetailsButton, SpeedMetricText, RemainingMetricText, FilesMetricText }) await CheckReachableAsync(control);
            report.Add($"Running {theme} {size.Width}×{size.Height} DIP: actions reachable and labels fit; filename ellipsis intentional.");
            if (capture)
            {
                MainContentScroll.ChangeView(null, 0, null, true);
                await SettleLayoutAsync();
                await CaptureLayoutAsync(Root, Path.Combine(captureDirectory, $"Running-{theme}-{size.Width}.png"));
            }
        }
        // Dialogs are real owned windows; check each one in both themes, then dismiss it.
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            Root.RequestedTheme = theme;
            await SettleLayoutAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Exception? inspectionError = null;
            async Task Inspect(AppDialog dialog, string name)
            {
                // Runs from the dialog's Loaded callback; keep the first failure and always dismiss.
                try
                {
                    await SettleLayoutAsync();
                    dialog.Root.UpdateLayout();
                    report.Add($"{name} {theme}: {CheckDialog(dialog)}");
                    foreach (var textName in new[] { "HeadingText", "BodyText", "FooterText" })
                    {
                        var text = (TextBlock)dialog.Root.FindName(textName);
                        if (text.FontFamily.Source != CurrentFileText.FontFamily.Source)
                            throw new InvalidOperationException($"{textName} uses a different text font family.");
                    }
                    report.Add($"{name}: heading/body/footer share main text font {CurrentFileText.FontFamily.Source}.");
                    CheckTextFits(dialog.Root);
                    if (capture) await CaptureLayoutAsync(dialog.Root, Path.Combine(captureDirectory, $"{name}-{theme}.png"));
                }
                catch (Exception ex) { inspectionError ??= new InvalidOperationException($"{name} {theme}: {ex.Message}", ex); }
                finally { dialog.Close(AppDialog.Dismissed); }
            }
            await AppDialogs.AboutAsync(this, "2.2.3", _ => throw new InvalidOperationException("Layout check must not open a URL."),
                deadline.Token, dialog => _ = Inspect(dialog, "About"));
            if (await AppDialogs.SettingsAsync(this, ThemePreference.System, deadline.Token,
                    dialog => _ = Inspect(dialog, "Settings")) is not null)
                throw new InvalidOperationException("Dismissed settings changed the theme.");
            if (await AppDialogs.ShutdownAsync(this, deadline.Token, dialog => _ = Inspect(dialog, "Shutdown")))
                throw new InvalidOperationException("Dismissed shutdown was authorized.");
            var results = Enumerable.Range(0, 8).Select(index => new DestinationSnapshot(
                $"D:\\{index}-" + new string('x', 220), 100, 100, 5, 0, index % 3 == 0 ? 1UL : 0UL, 0, 0,
                index == 7 ? DestinationPhase.Failed : DestinationPhase.Done, index == 7 ? new string('e', 900) : null,
                "", 0, 0)).ToArray();
            if (await AppDialogs.ResultsAsync(this, results, true, true, deadline.Token, dialog => _ = Inspect(dialog, "Results")))
                throw new InvalidOperationException("Dismissed results authorized saving.");
            await AppDialog.ShowAsync(this, new AppDialogSpec("Diálogo en una ventana estrecha",
                "Texto largo para comprobar el ajuste de líneas, los iconos y los botones cuando el ancho disponible se reduce.",
                ["Guardar diagnóstico completo", "Cancelar operación"], 1, 1, AppDialogIcon.Warning, WidthDip: 320),
                deadline.Token, dialog => _ = Inspect(dialog, "Narrow"));
            if (inspectionError is not null) throw inspectionError;
            if (deadline.IsCancellationRequested) throw new InvalidOperationException("A dialog did not open in time.");
        }
        // Exercise both directions across the breakpoint; a resize must not leave a stale grid row.
        foreach (var width in new[] { 760, 641, 640, 639, 540, 639, 640, 641, 760 })
        {
            ResizeForCurrentDpi(new SizeInt32(width, 540));
            await SettleLayoutAsync();
            if (Grid.GetRow(ShutdownCheck) != (Root.ActualWidth >= WideLayoutMinWidth ? 0 : 1))
                throw new InvalidOperationException("Responsive state was stale after resizing.");
        }
        report.Add("Resize sweep crossed 640 DIP in both directions without a stale action row.");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { Passed = true, Checks = report },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    // Opt-in render of this app's XAML only; no desktop or other application is captured.
    private static async Task CaptureLayoutAsync(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        using var pixels = DataReader.FromBuffer(await bitmap.GetPixelsAsync());
        var bytes = new byte[pixels.UnconsumedBufferLength];
        pixels.ReadBytes(bytes);
        await using var output = File.Create(path);
        using var stream = output.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
    }

    // Owned by this window, owner disabled while open, fully inside the monitor work area.
    private string CheckDialog(AppDialog dialog)
    {
        if (GetWindow(dialog.Handle, 4) != dialog.Owner) throw new InvalidOperationException("Dialog lost its owner.");
        if (IsWindowEnabled(dialog.Owner)) throw new InvalidOperationException("Dialog owner is not modal-disabled.");
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(dialog.AppWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        var position = dialog.AppWindow.Position;
        var size = dialog.AppWindow.Size;
        const int tolerance = 8; // DWM invisible resize borders
        if (position.X < area.X - tolerance || position.Y < area.Y - tolerance ||
            position.X + size.Width > area.X + area.Width + tolerance ||
            position.Y + size.Height > area.Y + area.Height + tolerance)
            throw new InvalidOperationException($"Dialog exceeds work area: {position.X},{position.Y} {size.Width}×{size.Height}.");
        if (dialog.Root.ActualHeight <= 0 || dialog.Root.ActualWidth <= 0)
            throw new InvalidOperationException("Dialog content was not laid out.");
        return $"owned/modal; {size.Width}×{size.Height}px; theme {dialog.Root.ActualTheme}";
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint window, uint command);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);

    private async Task SettleLayoutAsync()
    {
        await Task.Delay(80);
        Root.UpdateLayout();
    }

    private async Task CheckReachableAsync(FrameworkElement control)
    {
        // Scrolling is intentional for a short viewport; controls must be reachable, not silently clipped.
        MainContentScroll.ChangeView(null, 0, null, true);
        await SettleLayoutAsync();
        var bounds = control.TransformToVisual(Root).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
        var bottom = Root.ActualHeight - StatusFooter.ActualHeight;
        // Expanding options can remeasure while the scroll position changes. Keep
        // scrolling toward the control until it is wholly visible or no travel remains.
        for (var attempt = 0; attempt < 4 && bounds.Bottom > bottom + 1; attempt++)
        {
            var before = MainContentScroll.VerticalOffset;
            var desired = Math.Min(MainContentScroll.ScrollableHeight,
                before + Math.Max(16, bounds.Bottom - bottom + 8));
            if (desired <= before + 0.5) break;
            MainContentScroll.ChangeView(null, desired, null, true);
            await SettleLayoutAsync();
            bounds = control.TransformToVisual(Root).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
        }
        if (control.ActualWidth <= 0 || control.ActualHeight <= 0 || bounds.Left < -1 ||
            bounds.Right > Root.ActualWidth + 1 || bounds.Top < -1 || bounds.Bottom > bottom + 1)
            throw new InvalidOperationException($"Control cannot be reached: {control.Name} {bounds} in {Root.ActualWidth}×{Root.ActualHeight}; scroll offset {MainContentScroll.VerticalOffset}, scrollable {MainContentScroll.ScrollableHeight}, extent {MainContentScroll.ExtentHeight}, viewport {MainContentScroll.ViewportHeight}.");
        CheckTextFits(control);
    }

    private static void CheckTextFits(DependencyObject parent)
    {
        if (parent is TextBlock text && text.Visibility == Visibility.Visible && text.ActualWidth > 0 &&
            text.TextTrimming == TextTrimming.None && !string.IsNullOrEmpty(text.Text))
        {
            var probe = new TextBlock
            {
                Text = text.Text, FontSize = text.FontSize, FontFamily = text.FontFamily,
                FontWeight = text.FontWeight, FontStyle = text.FontStyle,
                TextWrapping = text.TextWrapping, Padding = text.Padding,
            };
            // TextBlock may arrange narrower than its slot after measuring a complete line.
            // Re-measuring that shrink-to-fit width can introduce a wrap that never occurred
            // in the real layout. Use the parent's allocated width, as the live measure does.
            var slot = LayoutInformation.GetLayoutSlot(text);
            var availableWidth = Math.Max(text.ActualWidth, slot.Width - text.Margin.Left - text.Margin.Right);
            probe.Measure(new Size(text.TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity : availableWidth,
                double.PositiveInfinity));
            if (probe.DesiredSize.Width > availableWidth + 1 || probe.DesiredSize.Height > text.ActualHeight + 1)
                throw new InvalidOperationException($"Text clipped: {text.Text} needs {probe.DesiredSize}, has {text.ActualWidth}×{text.ActualHeight}, slot {slot}, desired {text.DesiredSize}.");
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) CheckTextFits(VisualTreeHelper.GetChild(parent, i));
    }
}
