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

        Assert.IsTrue(xaml.Contains("x:Name=\"OverallProgressBar\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("ProgressList", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("RunningDestinationTitle", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("ProgressRow", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("_progressRows", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DefaultWindowUsesCompactFootprint()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));

        Assert.IsTrue(code.Contains("SizeInt32(840, 520)", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("MaxWidth=\"820\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("MaxHeight=\"108\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("<Expander Grid.Row=\"4\" Header=\"Opciones\" IsExpanded=\"False\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowUsesNativeWindowsChromeAndMica()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));

        Assert.IsTrue(xaml.Contains("x:Name=\"AppTitleBar\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("Background=\"Transparent\"", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("ExtendsContentIntoTitleBar = true", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("SetTitleBar(AppTitleBar)", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("SystemBackdrop = new MicaBackdrop()", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("ConfigureNativeWindowChrome()", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("AppWindow.TitleBar", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("ButtonBackgroundColor = Microsoft.UI.Colors.Transparent", StringComparison.Ordinal));
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
        Assert.IsFalse(code.Contains("Aggregate<DestinationSnapshot, ulong>(0, (sum, item) => checked(sum + item.Total))", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("Aggregate<DestinationSnapshot, ulong>(0, (sum, item) => checked(sum + item.VerifyBytesTotal))", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new AssertFailedException("No se encontró la raíz del repositorio.");
    }
}
