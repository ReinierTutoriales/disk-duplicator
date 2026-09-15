using System.Diagnostics;
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
    public void ReplayGateRequiresSustainedHighBacklogAndHystereticLowExit()
    {
        var gate = new BranchReplayGate(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        var frequency = Stopwatch.Frequency;
        static long At(long origin, TimeSpan elapsed) => origin + (long)(elapsed.TotalSeconds * Stopwatch.Frequency);
        var t0 = frequency;

        Assert.IsFalse(gate.ShouldReplay(1000, 1000, t0));
        Assert.IsFalse(gate.ShouldReplay(1000, 1000, At(t0, TimeSpan.FromMilliseconds(50))));
        Assert.IsTrue(gate.ShouldReplay(1000, 1000, At(t0, TimeSpan.FromMilliseconds(110))));
        Assert.IsTrue(gate.IsActive);

        Assert.IsTrue(gate.ShouldReplay(400, 1000, At(t0, TimeSpan.FromMilliseconds(120))));
        Assert.IsTrue(gate.ShouldReplay(400, 1000, At(t0, TimeSpan.FromMilliseconds(180))));
        Assert.IsFalse(gate.ShouldReplay(400, 1000, At(t0, TimeSpan.FromMilliseconds(230))));
        Assert.IsFalse(gate.IsActive);
    }

    [TestMethod]
    public void ReplayGateRejectsOneSampleSpike()
    {
        var gate = new BranchReplayGate(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        var t0 = Stopwatch.Frequency;
        Assert.IsFalse(gate.ShouldReplay(1000, 1000, t0));
        Assert.IsFalse(gate.ShouldReplay(100, 1000, t0 + Stopwatch.Frequency / 20));
        Assert.IsFalse(gate.ShouldReplay(1000, 1000, t0 + Stopwatch.Frequency / 10));
        Assert.IsFalse(gate.IsActive);
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