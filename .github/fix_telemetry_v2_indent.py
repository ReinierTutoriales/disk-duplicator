from pathlib import Path
p = Path('.github/apply_telemetry_stress_v2.py')
s = p.read_text(encoding='utf-8')
old = """replace_once(\n'''            if (!await worker.WaitForAdaptiveWindowAsync(job.Token).ConfigureAwait(false))\n            {\n                ReleaseIfData(message);\n                return;\n            }''',\n'''            var queueWaitStarted = Stopwatch.GetTimestamp();\n            var windowOpen = await worker.WaitForAdaptiveWindowAsync(job.Token).ConfigureAwait(false);\n            job.Telemetry.RecordQueueWait(Stopwatch.GetElapsedTime(queueWaitStarted));\n            if (!windowOpen)\n            {\n                ReleaseIfData(message);\n                return;\n            }''',\n'adaptive queue wait')"""
new = """replace_once(\n'''                if (!await worker.WaitForAdaptiveWindowAsync(job.Token).ConfigureAwait(false))\n                {\n                    ReleaseIfData(message);\n                    return;\n                }''',\n'''                var queueWaitStarted = Stopwatch.GetTimestamp();\n                var windowOpen = await worker.WaitForAdaptiveWindowAsync(job.Token).ConfigureAwait(false);\n                job.Telemetry.RecordQueueWait(Stopwatch.GetElapsedTime(queueWaitStarted));\n                if (!windowOpen)\n                {\n                    ReleaseIfData(message);\n                    return;\n                }''',\n'adaptive queue wait')"""
if s.count(old) != 1:
    raise SystemExit(f'v2 queue helper anchor mismatch: {s.count(old)}')
p.write_text(s.replace(old, new, 1), encoding='utf-8')
