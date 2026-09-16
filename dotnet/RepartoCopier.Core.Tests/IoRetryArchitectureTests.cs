using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class IoRetryArchitectureTests
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

}
