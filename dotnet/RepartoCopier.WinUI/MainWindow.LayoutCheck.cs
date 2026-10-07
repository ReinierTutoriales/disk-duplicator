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
            await CheckReachableAsync(IndependentReadsCheck);
            await CheckReachableAsync(VerifyCheck);
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
        // Native dialogs run on the real UI thread, with a real owner and no application actions.
        void InspectAndClose(nint window)
        {
            report.Add(NativeDialogLayoutProbe.Check(window, nativeOwner));
            NativeTaskDialog.PostMessage(window, NativeTaskDialog.ClickButton, NativeTaskDialog.Cancel, nint.Zero);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        NativeAppDialogs.About(nativeOwner, "2.1.1", _ => throw new InvalidOperationException("Layout check must not open a URL."), deadline.Token, InspectAndClose);
        if (NativeAppDialogs.Settings(nativeOwner, ThemePreference.System, deadline.Token, InspectAndClose) is not null)
            throw new InvalidOperationException("Cancelled settings changed the theme.");
        if (NativeAppDialogs.Shutdown(nativeOwner, deadline.Token, InspectAndClose))
            throw new InvalidOperationException("Cancelled shutdown was authorized.");
        var results = Enumerable.Range(0, 8).Select(index => new DestinationSnapshot(
            $"D:\\{index}-" + new string('x', 220), 100, 100, 5, 1, 0, 0, 0,
            DestinationPhase.Done, null, "", 0, 0)).ToArray();
        var pages = 0;
        if (NativeAppDialogs.Results(nativeOwner, results, true, false, deadline.Token, window =>
        {
            report.Add(NativeDialogLayoutProbe.Check(window, nativeOwner));
            NativeTaskDialog.PostMessage(window, NativeTaskDialog.ClickButton,
                ++pages < 3 ? 102 : NativeTaskDialog.Cancel, nint.Zero);
        })) throw new InvalidOperationException("Layout check authorized saving.");
        if (pages != 3) throw new InvalidOperationException("Native result pagination did not render all three pages.");
        NativeConflictDialog.Show(nativeOwner, new ExistingFilesConflictException(
            [new DestinationConflict("D:\\ISOS", 5, 5, [])]), deadline.Token, InspectAndClose);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { Passed = true, Checks = report },
            new JsonSerializerOptions { WriteIndented = true }));
    }

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
        var bottom = Root.ActualHeight - 24; // footer row
        if (bounds.Bottom > bottom)
        {
            MainContentScroll.ChangeView(null, MainContentScroll.VerticalOffset + bounds.Bottom - bottom + 2, null, true);
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
