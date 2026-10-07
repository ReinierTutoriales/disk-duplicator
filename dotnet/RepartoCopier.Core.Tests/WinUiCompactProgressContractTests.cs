using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class WinUiCompactProgressContractTests
{
    [TestMethod]
    public void RunningViewUsesOneThickOverallProgressBarAndNoPerDiskRows()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(xaml.Contains("x:Name=\"OverallProgressBar\" Height=\"10\" MinHeight=\"10\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("x:Key=\"ProgressBarTrackHeight\">10", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("ProgressList", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("_progressRows", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowUsesOfficialCompactDensityAndNativeTitleBar()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "App.xaml"));
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(app.Contains("Microsoft.UI.Xaml/DensityStyles/Compact.xaml", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("<TitleBar x:Name=\"AppTitleBar\"", StringComparison.Ordinal));
        // Default 760×540 DIP leaves room for the running view without scrolling; 540×320 stays the minimum.
        Assert.IsTrue(code.Contains("DefaultWidth = 760", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("DefaultHeight = 540", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("x:Name=\"RunningPanel\" Visibility=\"Collapsed\" Spacing=\"12\" VerticalAlignment=\"Top\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CopySpeedUsesTheSameLogicalCompletionProgressAsTheBar()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(code.Contains("_copyProgressRate.Observe(written)", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("diagnostics.SourceRead5sBytesPerSecond", StringComparison.Ordinal));
        // Slowest healthy destination, computed in the single per-tick pass.
        Assert.IsTrue(code.Contains("minWritten = Math.Min(minWritten, item.Written)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DestinationChipsWrapResponsivelyWithOptionalVerification()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        // Chips flow into as many columns as fit and wrap; the host scrolls only past ~3 rows.
        Assert.IsTrue(xaml.Contains("<UniformGridLayout MinItemWidth=\"168\" MinItemHeight=\"36\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("x:Name=\"DestinationListHost\" MaxHeight=\"124\"", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("WideLayoutMinWidth = 640", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("VerifyCheck", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("VerifyCheck", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("Verify: VerifyCheck.IsChecked == true", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("Interval = TimeSpan.FromMilliseconds(250)", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Width=\"86\"", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new AssertFailedException("No se encontró la raíz del repositorio.");
    }
}
