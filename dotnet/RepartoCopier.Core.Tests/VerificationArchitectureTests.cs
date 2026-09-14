using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class VerificationArchitectureTests
{
    [TestMethod]
    public void ProductionVerifierRequiresSharedByteBudgetAndPublishesItsPressure()
    {
        var verify = typeof(FastVerificationReader).GetMethod(
            "VerifyAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(verify);
        var parameterTypes = verify.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        CollectionAssert.Contains(parameterTypes, typeof(VerificationReadBudget));

        var diagnostics = typeof(CopyDiagnosticsSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.Contains(diagnostics, nameof(CopyDiagnosticsSnapshot.VerificationReadBudgetBytes));
        CollectionAssert.Contains(diagnostics, nameof(CopyDiagnosticsSnapshot.PeakVerificationReadBytes));
    }

    [TestMethod]
    public void VerificationHasNoFixedSizeOrQd2Thresholds()
    {
        var fields = typeof(FastVerificationReader)
            .GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(fields, "DirectThreshold");

        var methods = typeof(FastVerificationReader)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.Contains(methods, "ReadDirectAsync");
        CollectionAssert.Contains(methods, "ReadBufferedAsync");
        CollectionAssert.Contains(methods, "VerifyBufferedAsync");
    }

    [TestMethod]
    public void VerificationBudgetHasNoFixedGigabyteCeilingField()
    {
        var fields = typeof(VerificationReadBudget)
            .GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.Name)
            .ToArray();

        CollectionAssert.DoesNotContain(fields, "MaximumBudget");
        CollectionAssert.DoesNotContain(fields, "MaxBytes");
    }

    [TestMethod]
    public void PendingVerificationReadIsAValueType()
    {
        var pendingRead = typeof(FastVerificationReader).GetNestedType(
            "PendingRead",
            BindingFlags.NonPublic);

        Assert.IsNotNull(pendingRead);
        Assert.IsTrue(pendingRead.IsValueType, "PendingRead debe permanecer como readonly struct para no asignar un objeto por I/O pendiente.");
        Assert.IsTrue(pendingRead.IsDefined(typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute), inherit: false));
    }
}
