using System.ComponentModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class TerminalErrorFormatterTests
{
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
        StringAssert.Contains(message, "No se pudo escribir imagen.iso.");
    }

    [TestMethod]
    public void FormatSnapshotsIncludesFailedDestinationsForUi()
    {
        var snapshot = new DestinationSnapshot(
            @"E:\Copias",
            written: 0,
            total: 1024,
            filesDone: 0,
            filesTotal: 1,
            filesSkipped: 0,
            filesErrored: 1,
            verifiedBytes: 0,
            verifyBytesTotal: 0,
            verifyFilesDone: 0,
            verifyFilesTotal: 0,
            bytesPerSecond: 0,
            recentBytesPerSecond: 0,
            phase: DestinationPhase.Failed,
            error: "Falló la escritura terminal.",
            lastFile: "imagen.iso",
            queueDepth: 0,
            retries: 0);

        var message = TerminalErrorFormatter.FormatSnapshots([snapshot]);

        StringAssert.Contains(message, @"Destino: E:\Copias");
        StringAssert.Contains(message, "Archivo: imagen.iso");
        StringAssert.Contains(message, "Fase: Failed");
        StringAssert.Contains(message, "Falló la escritura terminal.");
    }

    [TestMethod]
    public void FailedPhasePersistsTerminalDiagnosticLog()
    {
        var root = Path.Combine(Path.GetTempPath(), $"repartocopier-terminal-log-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var progress = new DestinationProgress(root, total: 1024, filesTotal: 1);
            progress.SetLastFile("imagen.iso");

            progress.SetPhase(DestinationPhase.Failed, "Falló la escritura terminal.");

            var log = Path.Combine(StateLayout.StateDirectoryFor(root), "terminal-error.log");
            Assert.IsTrue(File.Exists(log), "El log terminal debe persistirse síncronamente al pasar a Failed.");
            var text = File.ReadAllText(log);
            StringAssert.Contains(text, root);
            StringAssert.Contains(text, "imagen.iso");
            StringAssert.Contains(text, "Falló la escritura terminal.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
