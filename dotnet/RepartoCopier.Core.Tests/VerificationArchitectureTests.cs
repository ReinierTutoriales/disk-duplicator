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
    public void VerificationBudgetHasNoFixedGigabyteCeilingField()
    {
        var fields = typeof(VerificationReadBudget)
            .GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.Name)
            .ToArray();

        CollectionAssert.DoesNotContain(fields, "MaximumBudget");
        CollectionAssert.DoesNotContain(fields, "MaxBytes");
    }
}
