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
        Assert.IsTrue(code.Contains("SizeInt32(720, 320)", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("VerticalAlignment=\"Top\" Margin=\"0,4,0,0\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CopyUiSeparatesLogicalProgressFromRealIoRates()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(code.Contains("var progressSpeed = paused ? 0d : _copyProgressRate.Observe(written)", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("diagnostics.SourceRead5sBytesPerSecond", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("snapshot.SustainedWrite5sBytesPerSecond", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("active.Min(item => item.Written)", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("Velocidad fuente", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("CurrentPathText.Text = FormatDestinationRates(snapshots)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DestinationChipsAreDenseAndVerificationIsNotAUserToggle()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(xaml.Contains("MinWidth=\"56\" MaxWidth=\"148\" Height=\"30\" Padding=\"5,0,2,0\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("Width=\"18\" Height=\"18\" Padding=\"0\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("Margin\" Value=\"0,0,2,0\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("VerifyCheck", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("VerifyCheck", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("Verify: true", StringComparison.Ordinal));
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
