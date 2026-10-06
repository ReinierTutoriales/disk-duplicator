using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class WinUiVerifyContractTests
{
    [TestMethod]
    public void VerificationChoiceIsVisibleAndPassedToTheEngine()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(xaml.Contains("x:Name=\"VerifyCheck\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("IsChecked=\"False\"", StringComparison.Ordinal));
        Assert.IsTrue(codeBehind.Contains("VerifyCheck.IsEnabled = enabled", StringComparison.Ordinal));
        Assert.IsTrue(codeBehind.Contains("Verify: VerifyCheck.IsChecked == true", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProfilesPreserveTheirVerificationChoice()
    {
        var root = FindRepositoryRoot();
        var profile = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "CopyProfile.cs"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(profile.Contains("bool VerifyAfterCopy = true", StringComparison.Ordinal));
        Assert.IsTrue(codeBehind.Contains("VerifyCheck.IsChecked = profile.VerifyAfterCopy", StringComparison.Ordinal));
        Assert.IsTrue(codeBehind.Contains("Verify: VerifyCheck.IsChecked == true", StringComparison.Ordinal));
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
