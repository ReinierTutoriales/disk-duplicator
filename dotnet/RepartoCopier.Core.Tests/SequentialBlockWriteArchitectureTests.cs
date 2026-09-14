using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SequentialBlockWriteArchitectureTests
{
    [TestMethod]
    public void DestinationCoordinatorDoesNotExposeIntraBlockDepthOrSliceControls()
    {
        var type = typeof(CopyEngine).Assembly.GetType("RepartoCopier.Core.DestinationWriteCoordinator", throwOnError: true)!;
        var write = type.GetMethod("WriteAsync", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("DestinationWriteCoordinator.WriteAsync no existe.");
        var names = write.GetParameters().Select(parameter => parameter.Name).ToArray();

        CollectionAssert.DoesNotContain(names, "requestedDepth");
        CollectionAssert.DoesNotContain(names, "minimumSliceBytes");
        CollectionAssert.Contains(names, "data");
        CollectionAssert.Contains(names, "scheduler");
    }

    [TestMethod]
    public void DirectWriterDoesNotExposeIntraBlockDepthOrSliceControls()
    {
        var type = typeof(CopyEngine).Assembly.GetType("RepartoCopier.Core.DirectIoDestinationWriter+Session", throwOnError: true)!;
        var write = type.GetMethod("WriteAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("DirectIoDestinationWriter.Session.WriteAsync no existe.");
        var names = write.GetParameters().Select(parameter => parameter.Name).ToArray();

        CollectionAssert.DoesNotContain(names, "requestedDepth");
        CollectionAssert.DoesNotContain(names, "minimumSliceBytes");
    }
}
