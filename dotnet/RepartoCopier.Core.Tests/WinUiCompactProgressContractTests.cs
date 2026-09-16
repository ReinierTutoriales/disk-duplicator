using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class WinUiCompactProgressContractTests
{
    [TestMethod]
    public void RunningViewUsesOnlyOverallJobProgress()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(xaml.Contains("OverallProgressBar", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("ProgressList", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("_progressRows", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PreparationViewIsCompactAndDestinationsAreHorizontal()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(code.Contains("SizeInt32(800, 460)", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("MaxWidth", StringComparison.Ordinal) && xaml.Contains("780", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("DestinationList", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("ItemsStackPanel Orientation", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("HorizontalScrollMode", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("Header=\"Opciones\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowUsesWindows11MicaLayeringAndInactiveTitleState()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(xaml.Contains("LayerFillColorDefaultBrush", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("ExtendsContentIntoTitleBar = true", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("SetTitleBar(AppTitleBar)", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("SystemBackdrop = new MicaBackdrop()", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("Activated += MainWindow_Activated", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("WindowActivationState.Deactivated", StringComparison.Ordinal));
    }

    [TestMethod]
    public void VerifyShowsRealSourceEquivalentSpeedAndEta()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        var telemetry = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyTelemetry.cs"));
        Assert.IsTrue(code.Contains("diagnostics.VerifyLogical5sBytesPerSecond", StringComparison.Ordinal));
        Assert.IsTrue(telemetry.Contains("VerifyLogical5sBytesPerSecond", StringComparison.Ordinal));
        Assert.IsTrue(telemetry.Contains("_verifyLogicalRate", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OverallProgressUsesLogicalJobBytesNotAggregateDestinationBytes()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(code.Contains("snapshots.Select(item => item.Total).DefaultIfEmpty(0UL).Max()", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("active.Min(item => item.Written)", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("snapshots.Select(item => item.VerifyBytesTotal).DefaultIfEmpty(0UL).Max()", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("verifyActive.Min(item => item.VerifiedBytes)", StringComparison.Ordinal));
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