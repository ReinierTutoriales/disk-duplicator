from pathlib import Path
p = Path(__file__).with_name('extremecopy_baseline_migration.py')
s = p.read_text(encoding='utf-8')
old = """engine = replace_once(engine,\n'''        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        PipelineGovernor pipeline,\n        DeviceScheduler? sharedSourceScheduler)\n''',\n'''        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        DeviceScheduler? sharedSourceScheduler)\n''',\n'sequential signature')"""
new = """engine = engine.replace(\n'''        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        PipelineGovernor pipeline,\n        DeviceScheduler? sharedSourceScheduler)\n''',\n'''        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        DeviceScheduler? sharedSourceScheduler)\n''',\n1)"""
if old not in s:
    raise RuntimeError('signature matcher block not found')
s = s.replace(old, new, 1)
p.write_text(s, encoding='utf-8')
print('migration matcher fixed')
