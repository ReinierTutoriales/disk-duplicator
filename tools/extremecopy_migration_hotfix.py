from pathlib import Path
p = Path(__file__).with_name('extremecopy_baseline_migration.py')
s = p.read_text(encoding='utf-8')
old = """engine = replace_once(engine,\n'''        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        PipelineGovernor pipeline,\n        DeviceScheduler? sharedSourceScheduler)\n''',\n'''        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        DeviceScheduler? sharedSourceScheduler)\n''',\n'sequential signature')"""
new = """engine = engine.replace(\n'''        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        PipelineGovernor pipeline,\n        DeviceScheduler? sharedSourceScheduler)\n''',\n'''        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        DeviceScheduler? sharedSourceScheduler)\n''',\n1)"""
if old not in s:
    raise RuntimeError('signature matcher block not found')
s = s.replace(old, new, 1)
old_guard = "all_cs = '\\n'.join(p.read_text(encoding='utf-8', errors='ignore') for p in (ROOT / 'dotnet').rglob('*.cs'))"
new_guard = "all_cs = '\\n'.join(p.read_text(encoding='utf-8', errors='ignore') for base in (CORE, UI) for p in base.rglob('*.cs'))"
if old_guard not in s:
    raise RuntimeError('product guard block not found')
s = s.replace(old_guard, new_guard, 1)
marker = "ENGINE.write_text(engine, encoding='utf-8')"
if marker not in s:
    raise RuntimeError('engine write marker not found')
s = s.replace(marker, "engine = engine.replace('InitialBufferBudget', 'SharedFanoutPoolBytes')\n" + marker, 1)

final_print = "print('ExtremeCopy-baseline shared FAN-OUT migration applied')"
if final_print not in s:
    raise RuntimeError('final print marker missing')
cleanup = r'''
# Remove tests whose production types were intentionally deleted, and retarget recovery assertions.
def remove_test_method(text, method_name):
    marker = f'    [TestMethod]\n    public '
    name_pos = text.find(method_name)
    if name_pos < 0:
        return text
    start = text.rfind('    [TestMethod]', 0, name_pos)
    if start < 0:
        raise RuntimeError(f'test start missing: {method_name}')
    end = text.find('    [TestMethod]', name_pos)
    if end < 0:
        end = text.rfind('}')
    return text[:start] + text[end:]

core_parity = TESTS / 'CoreParityTests.cs'
cp = core_parity.read_text(encoding='utf-8')
cp = remove_test_method(cp, 'PipelineGovernorCancellationDoesNotLeakPrefetchCapacity')
cp = remove_test_method(cp, 'PipelineGovernorRepeatedCancellationStressDoesNotLeakSlots')
core_parity.write_text(cp, encoding='utf-8')

progress_tests = TESTS / 'ProgressTelemetryTests.cs'
pt = progress_tests.read_text(encoding='utf-8')
pt = remove_test_method(pt, 'DiagnosticsSnapshotIncludesProductionPipelineGovernor')
progress_tests.write_text(pt, encoding='utf-8')

io_recovery = TESTS / 'IoRecoveryArchitectureTests.cs'
irt = io_recovery.read_text(encoding='utf-8')
irt = remove_test_method(irt, 'ReplayCanBeDisabledForPhysicalABWithoutChangingDefault')
old_method = '''    [TestMethod]\n    public void ProductivePathsDoNotContainLegacyFixedRetryDelayPolicy()\n    {\n        var root = FindRepositoryRoot();\n        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));\n        var verify = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "FastVerificationReader.cs"));\n        Assert.IsFalse(engine.Contains("private const int Retries = 2", StringComparison.Ordinal));\n        Assert.IsFalse(engine.Contains("Task.Delay(75 *", StringComparison.Ordinal));\n        Assert.IsTrue(engine.Contains("RecordTransientFailure", StringComparison.Ordinal));\n        Assert.IsTrue(verify.Contains("RecordTransientFailure", StringComparison.Ordinal));\n        Assert.IsTrue(verify.Contains("TransientIoErrorClassifier.IsTransient", StringComparison.Ordinal));\n    }\n'''
new_method = '''    [TestMethod]\n    public void ProductivePathsDoNotContainLegacyFixedRetryDelayPolicy()\n    {\n        var root = FindRepositoryRoot();\n        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));\n        Assert.IsFalse(engine.Contains("private const int Retries = 2", StringComparison.Ordinal));\n        Assert.IsFalse(engine.Contains("Task.Delay(75 *", StringComparison.Ordinal));\n        Assert.IsTrue(engine.Contains("RecordTransientFailure", StringComparison.Ordinal));\n        Assert.IsTrue(engine.Contains("TransientIoErrorClassifier.IsTransient", StringComparison.Ordinal));\n        Assert.IsFalse(File.Exists(Path.Combine(root, "dotnet", "RepartoCopier.Core", "FastVerificationReader.cs")));\n    }\n'''
if old_method not in irt:
    raise RuntimeError('legacy recovery test block missing')
irt = irt.replace(old_method, new_method, 1)
io_recovery.write_text(irt, encoding='utf-8')
'''
s = s.replace(final_print, cleanup + "\n" + final_print, 1)
p.write_text(s, encoding='utf-8')
print('migration cleanup extended to retired tests')
