from pathlib import Path

p = Path(__file__).with_name('extremecopy_baseline_migration.py')
s = p.read_text(encoding='utf-8')

# The old engine contains this signature twice. Replace only the first production
# occurrence instead of requiring a globally unique textual match.
old = """engine = replace_once(engine,\n'''        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        PipelineGovernor pipeline,\n        DeviceScheduler? sharedSourceScheduler)\n''',\n'''        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        DeviceScheduler? sharedSourceScheduler)\n''',\n'sequential signature')"""
new = """engine = engine.replace(\n'''        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        PipelineGovernor pipeline,\n        DeviceScheduler? sharedSourceScheduler)\n''',\n'''        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        DeviceScheduler? sharedSourceScheduler)\n''',\n1)"""
if old not in s:
    raise RuntimeError('signature matcher block not found')
s = s.replace(old, new, 1)

# Architecture guards inspect product sources only; tests may mention deleted types
# to assert that they stay deleted.
old_guard = "all_cs = '\\n'.join(p.read_text(encoding='utf-8', errors='ignore') for p in (ROOT / 'dotnet').rglob('*.cs'))"
new_guard = "all_cs = '\\n'.join(p.read_text(encoding='utf-8', errors='ignore') for base in (CORE, UI) for p in base.rglob('*.cs'))"
if old_guard not in s:
    raise RuntimeError('product guard block not found')
s = s.replace(old_guard, new_guard, 1)

# The source still had one sizing fallback using the retired constant.
marker = "ENGINE.write_text(engine, encoding='utf-8')"
if marker not in s:
    raise RuntimeError('engine write marker not found')
s = s.replace(marker, "engine = engine.replace('InitialBufferBudget', 'SharedFanoutPoolBytes')\n" + marker, 1)

# Remove tests whose production mechanisms are intentionally deleted. These are
# not compatibility tests; keeping them would force dead architecture back in.
final_print = "print('ExtremeCopy-baseline shared FAN-OUT migration applied')"
if final_print not in s:
    raise RuntimeError('final print marker missing')

cleanup = r'''
# Retire tests that target deleted architecture rather than current behavior.
def remove_test_method(text, method_name):
    name_pos = text.find(method_name)
    if name_pos < 0:
        return text
    start = text.rfind('    [TestMethod]', 0, name_pos)
    if start < 0:
        raise RuntimeError(f'test start missing: {method_name}')
    next_test = text.find('    [TestMethod]', name_pos)
    if next_test >= 0:
        end = next_test
    else:
        end = text.rfind('}')
        if end < 0:
            raise RuntimeError(f'class end missing: {method_name}')
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
irt = remove_test_method(irt, 'ProductivePathsDoNotContainLegacyFixedRetryDelayPolicy')
io_recovery.write_text(irt, encoding='utf-8')
'''

s = s.replace(final_print, cleanup + "\n" + final_print, 1)
p.write_text(s, encoding='utf-8')
print('migration matcher, guards, buffer symbol, and retired tests fixed')
