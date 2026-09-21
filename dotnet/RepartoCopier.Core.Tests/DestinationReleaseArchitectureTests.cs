using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class DestinationReleaseArchitectureTests
{
    [TestMethod]
    public void WriterClosesRecoveryHandlesBeforeAdvertisingSafeRemoval()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));

        var dispose = engine.IndexOf("recovery?.Dispose();", StringComparison.Ordinal);
        var release = engine.IndexOf("worker.ReleaseStateLease();", dispose, StringComparison.Ordinal);
        var ready = engine.IndexOf("worker.Progress.SetPhase(DestinationPhase.Releasable)", release, StringComparison.Ordinal);

        Assert.IsTrue(dispose >= 0, "El writer debe cerrar manifest/journal.");
        Assert.IsTrue(release > dispose, "El lease sólo puede soltarse después de cerrar recovery.");
        Assert.IsTrue(ready > release, "No se puede anunciar retiro seguro antes de liberar el lease.");
    }

    [TestMethod]
    public void VerificationClosesReadHandlesBeforeAdvertisingSafeRemoval()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));

        var verify = engine.IndexOf("private static async Task VerifyDestinationsAsync", StringComparison.Ordinal);
        var dispose = engine.IndexOf("foreach (var target in targets)\n                    target.Dispose();", verify, StringComparison.Ordinal);
        var release = engine.IndexOf("workers[slot].ReleaseStateLease();", dispose, StringComparison.Ordinal);
        var ready = engine.IndexOf("progress[slot].SetPhase(DestinationPhase.Releasable);", release, StringComparison.Ordinal);

        Assert.IsTrue(verify >= 0);
        Assert.IsTrue(dispose > verify, "La verificación debe cerrar sus handles.");
        Assert.IsTrue(release > dispose, "El lease debe soltarse después de cerrar handles de verificación.");
        Assert.IsTrue(ready > release, "El estado retirable debe publicarse al final.");
    }

    [TestMethod]
    public void SafeRemovalPhaseIsNotTreatedAsAnActiveCopyDestination()
    {
        var root = FindRepositoryRoot();
        var ui = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(ui.Contains("not DestinationPhase.Releasable", StringComparison.Ordinal));
        Assert.IsTrue(ui.Contains("DestinationPhase.Releasable", StringComparison.Ordinal));
        Assert.IsTrue(ui.Contains("para retirar", StringComparison.OrdinalIgnoreCase));
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
