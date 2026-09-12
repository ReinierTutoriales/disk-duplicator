from pathlib import Path

path = Path('.github/apply_telemetry_stress.py')
s = path.read_text(encoding='utf-8')

old = '''# Measure the adaptive destination-window wait, which is the actionable backpressure signal.
needle = '        await worker.WaitForAdaptiveWindowAsync(job.Token).ConfigureAwait(false);'
if needle not in s:
    raise SystemExit('adaptive queue wait anchor not found')
s = s.replace(needle,
''' + "'''" + '''        var queueWaitStarted = Stopwatch.GetTimestamp();\\n        await worker.WaitForAdaptiveWindowAsync(job.Token).ConfigureAwait(false);\\n        job.Telemetry.RecordQueueWait(Stopwatch.GetElapsedTime(queueWaitStarted));''' + "'''" + ''', 1)

# Reuse the existing whole-block write timer used by adaptive queue tuning.
needle = '                    worker.RecordBlockWrite(elapsed);'
if needle not in s:
    raise SystemExit('block write telemetry anchor not found')
s = s.replace(needle,
''' + "'''" + '''                    worker.RecordBlockWrite(elapsed);\\n                    job.Telemetry.RecordWrite(data.Block.Length, elapsed);''' + "'''" + ''', 1)
'''
new = '''# Measure the adaptive destination-window wait, which is the actionable backpressure signal.
needle = ''' + "'''" + '''            if (!await worker.WaitForAdaptiveWindowAsync(job.Token).ConfigureAwait(false))
            {
                ReleaseIfData(message);
                return;
            }''' + "'''" + '''
if needle not in s:
    raise SystemExit('adaptive queue wait anchor not found')
s = s.replace(needle,
''' + "'''" + '''            var queueWaitStarted = Stopwatch.GetTimestamp();
            var windowOpen = await worker.WaitForAdaptiveWindowAsync(job.Token).ConfigureAwait(false);
            job.Telemetry.RecordQueueWait(Stopwatch.GetElapsedTime(queueWaitStarted));
            if (!windowOpen)
            {
                ReleaseIfData(message);
                return;
            }''' + "'''" + ''', 1)

# Reuse the existing whole-block write timer used by adaptive queue tuning, but count every block.
needle = ''' + "'''" + '''                                if (chunkData.Block.Length >= WriteChunkSize)
                                    worker.RecordBlockWrite(elapsed);
                                current.Copied += chunkData.Block.Length;''' + "'''" + '''
if needle not in s:
    raise SystemExit('block write telemetry anchor not found')
s = s.replace(needle,
''' + "'''" + '''                                if (chunkData.Block.Length >= WriteChunkSize)
                                    worker.RecordBlockWrite(elapsed);
                                job.Telemetry.RecordWrite(chunkData.Block.Length, elapsed);
                                current.Copied += chunkData.Block.Length;''' + "'''" + ''', 1)
'''
if old not in s:
    raise SystemExit('telemetry helper queue/write section not found')
s = s.replace(old, new, 1)

s = s.replace(
"s = s.replace('FinishFile(worker, current, end.Hash, recovery);', 'FinishFile(worker, current, end.Hash, recovery, job);', 1)",
"s = s.replace('FinishFile(worker, current, end.Hash, options, recovery);', 'FinishFile(worker, current, end.Hash, options, recovery, job);', 1)",
1)

old_sig = """s = s.replace(\n'''        byte[] hash,\\n        RecoveryCheckpointWriter recovery)''',\n'''        byte[] hash,\\n        RecoveryCheckpointWriter recovery,\\n        CopyJob job)''', 1)"""
new_sig = """s = s.replace(\n'''        byte[] expectedHash,\\n        CopyOptions options,\\n        RecoveryCheckpointWriter recovery)''',\n'''        byte[] expectedHash,\\n        CopyOptions options,\\n        RecoveryCheckpointWriter recovery,\\n        CopyJob job)''', 1)"""
if old_sig not in s:
    raise SystemExit('FinishFile signature helper anchor not found')
s = s.replace(old_sig, new_sig, 1)

s = s.replace("anchor = '''            hash);'''", "anchor = '''            expectedHash);'''", 1)

path.write_text(s, encoding='utf-8')
