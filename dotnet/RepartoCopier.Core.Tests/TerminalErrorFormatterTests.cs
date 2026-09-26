using System.ComponentModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class TerminalErrorFormatterTests
{
    [TestInitialize]
    public void Initialize()
    {
        Environment.SetEnvironmentVariable(StoragePreallocationPolicy.DisablePreallocationEnvironmentVariable, null);
        StoragePreallocationPolicy.ResetDiagnosticsForTests();
    }

    [TestCleanup]
    public void Cleanup()
    {
        TerminalFailureLog.SetLocalLogDirectoryForTests(null);
        Environment.SetEnvironmentVariable(StoragePreallocationPolicy.DisablePreallocationEnvironmentVariable, null);
        StoragePreallocationPolicy.ResetDiagnosticsForTests();
    }

    [TestMethod]
    public void FormatIncludesHResultNativeCodeDestinationAndFile()
    {
        var error = new IOException("outer", new Win32Exception(1117));

        var message = TerminalErrorFormatter.Format(
            error,
            destination: @"E:\Copias",
            file: "imagen.iso",
            phase: "copy-write");

        StringAssert.Contains(message, @"Destino: E:\Copias");
        StringAssert.Contains(message, "Archivo: imagen.iso");
        StringAssert.Contains(message, "Fase: copy-write");
        StringAssert.Contains(message, "preallocation=enabled_default");
        StringAssert.Contains(message, "HResult: 0x");
        StringAssert.Contains(message, "Win32 Native Error: 1117");
    }

    [TestMethod]
    public void FormatSnapshotErrorPreservesTerminalSnapshotText()
    {
        var message = TerminalErrorFormatter.FormatSnapshotError(
            "No se pudo escribir imagen.iso.",
            destination: @"E:\Copias",
            file: "imagen.iso",
            phase: "Failed");

        StringAssert.Contains(message, @"Destino: E:\Copias");
        StringAssert.Contains(message, "Archivo: imagen.iso");
        StringAssert.Contains(message, "Fase: Failed");
        StringAssert.Contains(message, "preallocation=enabled_default");
        StringAssert.Contains(message, "No se pudo escribir imagen.iso.");
    }

    [TestMethod]
    public void FormatSnapshotsIncludesFailedDestinationsForUi()
    {
        var snapshot = new DestinationSnapshot(
            @"E:\Copias",
            0,
            1024,
            0,
            1,
            0,
            1,
            0,
            0,
            0,
            0,
            0,
            0,
            DestinationPhase.Failed,
            "Falló la escritura terminal.",
            "imagen.iso",
            0,
            0);

        var message = TerminalErrorFormatter.FormatSnapshots([snapshot]);

        StringAssert.Contains(message, @"Destino: E:\Copias");
        StringAssert.Contains(message, "Archivo: imagen.iso");
        StringAssert.Contains(message, "Fase: Failed");
        StringAssert.Contains(message, "preallocation=enabled_default");
        StringAssert.Contains(message, "Falló la escritura terminal.");
    }

    [TestMethod]
    public void FailedPhasePersistsTerminalDiagnosticLogToLocalSinkFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), $"repartocopier-terminal-log-{Guid.NewGuid():N}");
        var local = Path.Combine(Path.GetTempPath(), $"repartocopier-local-terminal-log-{Guid.NewGuid():N}");
        TerminalFailureLog.SetLocalLogDirectoryForTests(local);
        try
        {
            Directory.CreateDirectory(root);
            var progress = new DestinationProgress(root, total: 1024, filesTotal: 1);
            progress.SetLastFile("imagen.iso");

            progress.SetPhase(DestinationPhase.Failed, "Falló la escritura terminal.");

            var log = Path.Combine(local, TerminalFailureLog.FileName);
            Assert.IsTrue(File.Exists(log), "El log terminal local debe persistirse síncronamente al pasar a Failed.");
            var text = File.ReadAllText(log);
            StringAssert.Contains(text, "utc=");
            StringAssert.Contains(text, "head_sha=");
            StringAssert.Contains(text, $"destination={root}");
            StringAssert.Contains(text, "preallocation=enabled_default");
            StringAssert.Contains(text, "imagen.iso");
            StringAssert.Contains(text, "Falló la escritura terminal.");
        }
        finally
        {
            TerminalFailureLog.SetLocalLogDirectoryForTests(null);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(local)) Directory.Delete(local, recursive: true);
        }
    }

    [TestMethod]
    public void TerminalLogPayloadIncludesDisabledPreallocationState()
    {
        Environment.SetEnvironmentVariable(StoragePreallocationPolicy.DisablePreallocationEnvironmentVariable, "1");
        StoragePreallocationPolicy.ResetDiagnosticsForTests();

        var payload = TerminalFailureLog.BuildPayload(@"E:\Copias", "Falló la escritura terminal.");

        StringAssert.Contains(payload, "preallocation=disabled_by_env");
        StringAssert.Contains(payload, @"destination=E:\Copias");
    }

    [TestMethod]
    public void TerminalLogFailureDoesNotMaskOriginalTerminalError()
    {
        var localParent = Path.Combine(Path.GetTempPath(), $"repartocopier-local-parent-{Guid.NewGuid():N}");
        var blockingFile = Path.Combine(localParent, "not-a-directory");
        Directory.CreateDirectory(localParent);
        File.WriteAllText(blockingFile, "blocks local log directory creation");
        TerminalFailureLog.SetLocalLogDirectoryForTests(Path.Combine(blockingFile, "child"));
        try
        {
            var original = "Falló la escritura terminal original.";
            var progress = new DestinationProgress("?:\\destino-invalido", total: 1024, filesTotal: 1);
            progress.SetLastFile("imagen.iso");

            progress.SetPhase(DestinationPhase.Failed, original);

            var snapshot = progress.Snapshot();
            Assert.AreEqual(DestinationPhase.Failed, snapshot.Phase);
            Assert.AreEqual(original, snapshot.Error);
        }
        finally
        {
            TerminalFailureLog.SetLocalLogDirectoryForTests(null);
            if (Directory.Exists(localParent)) Directory.Delete(localParent, recursive: true);
        }
    }
}
