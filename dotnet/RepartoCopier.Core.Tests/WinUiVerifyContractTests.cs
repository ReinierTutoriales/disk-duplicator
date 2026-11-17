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
    public void CompletionSummaryFailureNeverCountsAsSuccessOrLeavesRunningControls()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        var start = code.IndexOf("private async Task ObserveJobCompletionAsync", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, "ObserveJobCompletionAsync no existe.");
        var method = code[start..code.IndexOf("private static readonly TimeSpan ForegroundRefresh", start, StringComparison.Ordinal)];
        // An exception while building the summary must not offer the shutdown that only follows a success.
        Assert.IsTrue(method.Contains("var completedWithErrors = true;", StringComparison.Ordinal));
        // The restoration is the finally that follows the summary's catch (the only unconditional error state).
        var summaryCatch = method.IndexOf("_taskbar.Set(TaskbarState.Error, 100);", StringComparison.Ordinal);
        Assert.IsTrue(summaryCatch >= 0, "El catch del resumen debe marcar el error en la barra de tareas.");
        var restore = method[method.IndexOf("finally", summaryCatch, StringComparison.Ordinal)..];
        foreach (var required in new[]
                 {
                     "_progressTimer.Stop();", "PauseButton.IsEnabled = false;", "CancelButton.IsEnabled = false;",
                     "_job = null;", "SetEditingEnabled(true);", "ReleasePower();",
                 })
            Assert.IsTrue(restore.Contains(required, StringComparison.Ordinal), $"Falta en la restauración: {required}");
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
