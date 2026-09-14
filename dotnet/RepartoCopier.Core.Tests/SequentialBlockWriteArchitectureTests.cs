using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SequentialBlockWriteArchitectureTests
{
    [TestMethod]
    public void DestinationCoordinatorAcceptsOnlyWholeBlockWriteControls()
    {
        var type = typeof(CopyEngine).Assembly.GetType("RepartoCopier.Core.DestinationWriteCoordinator", throwOnError: true)!;
        var write = type.GetMethod("WriteAsync", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("DestinationWriteCoordinator.WriteAsync no existe.");
        var names = write.GetParameters().Select(parameter => parameter.Name).ToArray();

        CollectionAssert.AreEqual(
            new[] { "handle", "data", "baseOffset", "scheduler", "token", "requiredAlignment" },
            names,
            "El coordinador no debe recuperar controles de depth/slicing dentro de un bloque FAN-OUT.");
        Assert.AreEqual(typeof(Task<int>), write.ReturnType);
    }

    [TestMethod]
    public void DirectWriterAcceptsWholePayloadWithoutIntraBlockDepthOrSliceControls()
    {
        var type = typeof(CopyEngine).Assembly.GetType("RepartoCopier.Core.DirectIoDestinationWriter+Session", throwOnError: true)!;
        var write = type.GetMethod("WriteAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("DirectIoDestinationWriter.Session.WriteAsync no existe.");
        var names = write.GetParameters().Select(parameter => parameter.Name).ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "data",
                "fileOffset",
                "logicalFileLength",
                "payloadIsAligned",
                "scheduler",
                "token",
            },
            names,
            "La ruta Direct I/O no debe volver a fragmentar cada bloque para fabricar queue depth.");
        Assert.AreEqual(typeof(Task<int>), write.ReturnType);
    }
}
