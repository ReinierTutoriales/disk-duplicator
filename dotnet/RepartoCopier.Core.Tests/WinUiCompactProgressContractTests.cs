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
