from pathlib import Path


def replace_exact(text: str, old: str, new: str, expected: int, label: str) -> str:
    count = text.count(old)
    if count != expected:
        raise RuntimeError(f"{label}: expected {expected} matches, found {count}")
    return text.replace(old, new)

# Collapse the SkipSame hash helper to the only live contract: ResourceGovernor is
# always supplied by BuildVerifiedSkipMasksAsync, so the nullable/default branch
# is dead and must not coexist with the production path.
engine_path = Path("dotnet/RepartoCopier.Core/CopyEngine.cs")
engine = engine_path.read_text(encoding="utf-8")
engine = replace_exact(
    engine,
    "        ResourceGovernor? resources = null)\n",
    "        ResourceGovernor resources)\n",
    1,
    "make SkipSame governor mandatory")
old_branch = '''            if (resources is null)\n            {\n                hasher.UpdateWithJoin(buffer.Memory.Span[..read]);\n            }\n            else\n            {\n                using var lease = await resources.EnterCpuWorkAsync(token).ConfigureAwait(false);\n                hasher.UpdateWithJoin(buffer.Memory.Span[..read]);\n            }\n'''
new_branch = '''            using var lease = await resources.EnterCpuWorkAsync(token).ConfigureAwait(false);\n            hasher.UpdateWithJoin(buffer.Memory.Span[..read]);\n'''
engine = replace_exact(engine, old_branch, new_branch, 1, "remove dead nullable governor branch")
engine_path.write_text(engine, encoding="utf-8", newline="\n")

# PendingRead.Offset was carried but never consumed. Keep only state needed to
# validate the block and release its buffer.
verify_path = Path("dotnet/RepartoCopier.Core/FastVerificationReader.cs")
verify = verify_path.read_text(encoding="utf-8")
verify = replace_exact(
    verify,
    "                pending.Enqueue(new PendingRead(offset, expected, lease, started, task));\n",
    "                pending.Enqueue(new PendingRead(expected, lease, started, task));\n",
    1,
    "remove unused pending-read offset argument")
verify = replace_exact(
    verify,
    '''    private sealed record PendingRead(\n        long Offset,\n        VerificationBlock Expected,\n        SourceBufferLease Buffer,\n        long Started,\n        Task<int> Read);\n''',
    '''    private sealed record PendingRead(\n        VerificationBlock Expected,\n        SourceBufferLease Buffer,\n        long Started,\n        Task<int> Read);\n''',
    1,
    "remove unused pending-read offset field")
verify_path.write_text(verify, encoding="utf-8", newline="\n")

# Strengthen the existing H-11 contract: HashFileAsync must remain exactly three
# required parameters, with no optional/null bypass reintroduced later.
test_path = Path("dotnet/RepartoCopier.Core.Tests/DirectIoSourceReaderTests.cs")
test = test_path.read_text(encoding="utf-8")
old = '''        Assert.AreEqual(typeof(CancellationToken), parameters[1].ParameterType);\n        Assert.AreEqual("ResourceGovernor", parameters[2].ParameterType.Name);\n'''
new = '''        Assert.AreEqual(typeof(CancellationToken), parameters[1].ParameterType);\n        Assert.AreEqual("ResourceGovernor", parameters[2].ParameterType.Name);\n        Assert.IsFalse(parameters[2].HasDefaultValue);\n'''
test = replace_exact(test, old, new, 1, "strengthen SkipSame hash contract")
test_path.write_text(test, encoding="utf-8", newline="\n")

print("H-12 unification cleanup applied")
