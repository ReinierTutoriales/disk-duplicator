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
p.write_text(s, encoding='utf-8')
print('migration matcher, guards, and legacy buffer symbol fixed')
