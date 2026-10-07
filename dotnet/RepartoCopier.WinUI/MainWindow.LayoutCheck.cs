using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RepartoCopier.Core;
using Windows.Foundation;
using Windows.Graphics;

namespace RepartoCopier.WinUI;

public sealed partial class MainWindow
{
    internal async Task RunLayoutCheckAsync(string reportPath)
    {
        var report = new List<string>();
        var loaded = new TaskCompletionSource();
        if (Root.IsLoaded) loaded.SetResult();
        else Root.Loaded += (_, _) => loaded.TrySetResult();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var nativeOwner = WinRT.Interop.WindowNative.GetWindowHandle(this);
        report.Add($"Actual UI DPI: {GetDpiForWindow(nativeOwner)}. Other scale factors have geometry unit tests; not simulated render claims.");
        SourcePathBox.Text = @"H:\Una carpeta con nombres largos\ISOS";
        foreach (var drive in new[] { "D", "F", "G", "I", "J" }) _destinations.Add(new DestinationRow($"{drive}:\\ISOS"));
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        foreach (var size in new[] { new SizeInt32(540, 320), new SizeInt32(720, 320), new SizeInt32(1200, 720) })
        {
            Root.RequestedTheme = theme;
            ResizeForCurrentDpi(size);
            ShowPreparationView();
            await SettleLayoutAsync();
            foreach (var control in new FrameworkElement[] { StartButton, PickSourceFileButton, PickSourceFolderButton,
                AddDestinationsButton, ClearDestinationsButton }) await CheckReachableAsync(control);
            OptionsExpander.IsExpanded = true;
            await SettleLayoutAsync();
            await CheckReachableAsync(VerifyCheck);
            await CheckReachableAsync(KeepGoingCheck);
            OptionsExpander.IsExpanded = false;
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
        }
        // Dialogs are real owned windows; check each one in both themes, then dismiss it.
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            Root.RequestedTheme = theme;
            await SettleLayoutAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            async Task Inspect(AppDialog dialog, string name)
            {
                await SettleLayoutAsync();
                dialog.Root.UpdateLayout();
                report.Add($"{name} {theme}: {CheckDialog(dialog)}");
                CheckTextFits(dialog.Root);
                dialog.Close(AppDialog.Dismissed);
            }
            await AppDialogs.AboutAsync(this, "2.1.1", _ => throw new InvalidOperationException("Layout check must not open a URL."),
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
            if (deadline.IsCancellationRequested) throw new InvalidOperationException("A dialog did not open in time.");
        }
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { Passed = true, Checks = report },
            new JsonSerializerOptions { WriteIndented = true }));
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
        var bottom = Root.ActualHeight - 28; // footer row
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
