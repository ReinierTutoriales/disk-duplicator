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
}
