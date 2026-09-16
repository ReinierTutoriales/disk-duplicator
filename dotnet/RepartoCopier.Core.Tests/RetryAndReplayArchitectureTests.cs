using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class RetryAndReplayArchitectureTests
{
    [TestMethod]
    [DataRow(32, true)]
    [DataRow(33, true)]
    [DataRow(54, true)]
    [DataRow(64, true)]
    [DataRow(121, true)]
    [DataRow(1231, true)]
    [DataRow(1237, true)]
    [DataRow(3, false)]
    [DataRow(23, false)]
    [DataRow(1117, false)]
    public void TransientClassifierMatchesRetryContract(int win32Code, bool expected)
    {
        var error = new IOException("synthetic", unchecked((int)(0x80070000u | (uint)win32Code)));
        Assert.AreEqual(expected, TransientIoErrorClassifier.IsTransient(error));
    }

    [TestMethod]
    public void CancellationAndNonIoFailuresAreNeverTransient()
    {
        Assert.IsFalse(TransientIoErrorClassifier.IsTransient(new OperationCanceledException()));
        Assert.IsFalse(TransientIoErrorClassifier.IsTransient(new UnauthorizedAccessException()));
        Assert.IsFalse(TransientIoErrorClassifier.IsTransient(new InvalidOperationException()));
    }

    [TestMethod]
    public void BufferedProductionWriteActuallyCallsTransientClassifier()
    {
        var copyEngine = typeof(CopyEngine);
        var method = copyEngine.GetMethod("WriteBlockAtOffsetAsync", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("WriteBlockAtOffsetAsync no existe.");
        var target = typeof(TransientIoErrorClassifier).GetMethod("IsTransient", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("TransientIoErrorClassifier.IsTransient no existe.");
        var stateMachine = method
            .GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()
            ?.StateMachineType
            ?? throw new AssertFailedException("WriteBlockAtOffsetAsync debe conservar su state machine async.");
        var moveNext = stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new AssertFailedException("No se encontró MoveNext de WriteBlockAtOffsetAsync.");
        var il = moveNext.GetMethodBody()?.GetILAsByteArray()
            ?? throw new AssertFailedException("MoveNext de WriteBlockAtOffsetAsync no expone IL.");
        var token = BitConverter.GetBytes(target.MetadataToken);
        var found = false;
        for (var i = 0; i <= il.Length - token.Length; i++)
        {
            if (il.AsSpan(i, token.Length).SequenceEqual(token))
            {
                found = true;
                break;
            }
        }
        Assert.IsTrue(found, "La ruta buffered productiva debe consumir TransientIoErrorClassifier.IsTransient; no basta con que el clasificador exista.");
    }

    [TestMethod]
    public void ReplayPlacementRequiresExactDifferentPhysicalDeviceFromEveryParticipant()
    {
        var source = Device("C:\\source", 1);
        var destinations = new[] { Device("D:\\dest", 2), Device("E:\\dest", 3) };
        Assert.IsTrue(BranchReplayPlacement.IsSafePhysicalPlacement(Device("F:\\temp", 4), source, destinations));
        Assert.IsFalse(BranchReplayPlacement.IsSafePhysicalPlacement(Device("C:\\temp", 1), source, destinations));
        Assert.IsFalse(BranchReplayPlacement.IsSafePhysicalPlacement(Device("D:\\temp", 2), source, destinations));

        var unknownTemp = Device("F:\\temp", null);
        Assert.IsFalse(BranchReplayPlacement.IsSafePhysicalPlacement(unknownTemp, source, destinations));
        var unknownDestination = new[] { Device("D:\\dest", null) };
        Assert.IsFalse(BranchReplayPlacement.IsSafePhysicalPlacement(Device("F:\\temp", 4), source, unknownDestination));
    }

    [TestMethod]
    public void BranchIsolationReplacesTimedReplayGateWithQueueWindowFeedback()
    {
        var target = BranchIsolationPolicy.SharedRetentionTargetBytes(4 * 1024 * 1024, 8, 256L * 1024 * 1024);
        Assert.AreEqual(32L * 1024 * 1024, target);
        Assert.IsFalse(BranchIsolationPolicy.ShouldDetach(target, 4 * 1024 * 1024, 8, 256L * 1024 * 1024));
        Assert.IsTrue(BranchIsolationPolicy.ShouldDetach(target + 1, 4 * 1024 * 1024, 8, 256L * 1024 * 1024));
    }

    private static StorageDeviceInfo Device(string root, uint? physicalDeviceNumber) =>
        new(
            root,
            Path.GetPathRoot(root) ?? root,
            physicalDeviceNumber,
            1,
            "Synthetic",
            StorageMediaKind.SolidState,
            false,
            4096,
            4096,
            physicalDeviceNumber.HasValue,
            null);
}