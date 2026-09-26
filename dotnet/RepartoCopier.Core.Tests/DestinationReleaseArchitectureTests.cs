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
        var release = engine.IndexOf("worker.MarkSuccessfullyReleased();", dispose, StringComparison.Ordinal);
        var ready = engine.IndexOf("worker.Progress.SetPhase(DestinationPhase.Releasable)", release, StringComparison.Ordinal);

        Assert.IsTrue(dispose >= 0, "El writer debe cerrar manifest/journal.");
        Assert.IsTrue(release > dispose, "El lease sólo puede soltarse después de cerrar recovery.");
        Assert.IsTrue(ready > release, "No se puede anunciar retiro seguro antes de liberar el lease.");
    }

    [TestMethod]
    public void RecoveryCloseFailureStillReleasesLeaseWithoutAdvertisingSafeRemoval()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));

        var closeError = engine.IndexOf("Exception? recoveryCloseError = null;", StringComparison.Ordinal);
        var fail = engine.IndexOf("worker.Fail($\"No se pudo cerrar el estado de recuperación:", closeError, StringComparison.Ordinal);
        var readyGuard = engine.IndexOf("worker.IsActive && recoveryCloseError is null", fail, StringComparison.Ordinal);
        var safeRelease = engine.IndexOf("worker.MarkSuccessfullyReleased();", readyGuard, StringComparison.Ordinal);
        var failureRelease = engine.IndexOf("worker.ReleaseStateLease();", safeRelease, StringComparison.Ordinal);

        Assert.IsTrue(closeError >= 0);
        Assert.IsTrue(fail > closeError, "Un fallo al cerrar recovery debe fallar sólo ese destino.");
        Assert.IsTrue(readyGuard > fail, "La liberación exitosa debe estar protegida por cierre recovery correcto.");
        Assert.IsTrue(safeRelease > readyGuard, "Sólo el camino exitoso puede anunciar liberación completa.");
        Assert.IsTrue(failureRelease > safeRelease, "El camino fallido debe soltar el lease sin anunciar retiro seguro.");
    }

    [TestMethod]
    public void VerificationClosesReadHandlesBeforeAdvertisingSafeRemoval()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));

        var verify = engine.IndexOf("private static async Task VerifyDestinationsAsync", StringComparison.Ordinal);
        Assert.IsTrue(verify >= 0);
        var dispose = engine.IndexOf("target.Dispose();", verify, StringComparison.Ordinal);
        Assert.IsTrue(dispose > verify, "La verificación debe cerrar sus handles.");
        var release = engine.IndexOf("workers[slot].MarkSuccessfullyReleased();", dispose, StringComparison.Ordinal);
        var ready = engine.IndexOf("progress[slot].SetPhase(DestinationPhase.Releasable);", release, StringComparison.Ordinal);

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

    [TestMethod]
    public void VerificationFailureDrainsWritersAndPreservesAlreadyReleasedDestinations()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));

        var capture = engine.IndexOf("verificationError = ex;", StringComparison.Ordinal);
        var drainLoop = engine.IndexOf("while (pendingWriters.Count != 0)", StringComparison.Ordinal);
        var rethrow = engine.IndexOf("ExceptionDispatchInfo.Capture(verificationError).Throw()", StringComparison.Ordinal);
        Assert.IsTrue(drainLoop >= 0);
        Assert.IsTrue(capture > drainLoop, "El fallo de verificación debe capturarse dentro del drenaje de writers.");
        Assert.IsTrue(rethrow > capture, "El fallo sólo puede propagarse después de terminar el drenaje.");

        Assert.IsTrue(
            engine.Contains("and not DestinationPhase.Releasable", StringComparison.Ordinal),
            "Un fallo ajeno no debe convertir un destino ya liberado en Failed.");
        Assert.IsTrue(
            engine.Contains("and not DestinationPhase.Done", StringComparison.Ordinal),
            "Un fallo ajeno no debe sobrescribir un destino ya terminado.");
    }

    [TestMethod]
    public void SuccessfulReleaseRetiresDestinationFromLiveSpillFairShare()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));

        Assert.IsTrue(engine.Contains("public void MarkSuccessfullyReleased()", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("DeactivateDestination();", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("workers[slot].MarkSuccessfullyReleased();", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("worker.MarkSuccessfullyReleased();", StringComparison.Ordinal));
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
