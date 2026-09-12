from pathlib import Path

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')

# CPU governor must gate CPU hashing work, not an entire destination including asynchronous I/O.
s = s.replace(
'''        var tasks = activeSlots.Select(async slot =>
        {
            using var lease = await resources.EnterCpuWorkAsync(job.Token).ConfigureAwait(false);
            progress[slot].SetPhase(DestinationPhase.Verifying);
''',
'''        var tasks = activeSlots.Select(async slot =>
        {
            progress[slot].SetPhase(DestinationPhase.Verifying);
''', 1)

old_verify_hash = '''                var destination = Path.Combine(workers[slot].Root, entry.RelativePath);
                if (!File.Exists(destination))
                {
                    workers[slot].Fail($"Falta el archivo durante verificación: {destination}");
                    break;
                }
                var actual = await HashFileAsync(destination, job.Token).ConfigureAwait(false);
'''
new_verify_hash = '''                var destination = Path.Combine(workers[slot].Root, entry.RelativePath);
                if (!File.Exists(destination))
                {
                    workers[slot].Fail($"Falta el archivo durante verificación: {destination}");
                    break;
                }
                ValidateRuntimeDestinationPath(workers[slot].Root, entry.RelativePath);
                WindowsPath.EnsureRegularFile(destination, "El archivo durante verificación");
                if (new FileInfo(destination).Length != entry.Size)
                {
                    workers[slot].Fail($"Tamaño no coincide durante verificación: {destination}");
                    break;
                }
                var actual = await HashFileAsync(destination, job.Token, resources).ConfigureAwait(false);
'''
if old_verify_hash not in s:
    raise SystemExit('verify hash anchor not found')
s = s.replace(old_verify_hash, new_verify_hash, 1)

# Skip-same source hashing is also CPU work. Snapshot the source on both sides of the hash.
s = s.replace(
'''            var sourceHash = await HashFileAsync(entry.SourcePath, token).ConfigureAwait(false);
            var checks = candidates.Select(async slot =>
            {
                using var lease = await resources.EnterCpuWorkAsync(token).ConfigureAwait(false);
                var destination = Path.Combine(copy.DestinationRoots[slot], entry.RelativePath);
                var destinationHash = await HashFileAsync(destination, token).ConfigureAwait(false);
''',
'''            ValidateSourceSnapshot(entry);
            var sourceHash = await HashFileAsync(entry.SourcePath, token, resources).ConfigureAwait(false);
            ValidateSourceSnapshot(entry);
            var checks = candidates.Select(async slot =>
            {
                var destination = Path.Combine(copy.DestinationRoots[slot], entry.RelativePath);
                ValidateRuntimeDestinationPath(copy.DestinationRoots[slot], entry.RelativePath);
                WindowsPath.EnsureRegularFile(destination, "El archivo candidato de SkipSame");
                if (new FileInfo(destination).Length != entry.Size)
                    return;
                var destinationHash = await HashFileAsync(destination, token, resources).ConfigureAwait(false);
''', 1)

old_hash_sig = '''    private static async Task<byte[]> HashFileAsync(string path, CancellationToken token)
    {
'''
new_hash_sig = '''    private static async Task<byte[]> HashFileAsync(
        string path,
        CancellationToken token,
        ResourceGovernor? resources = null)
    {
'''
if old_hash_sig not in s:
    raise SystemExit('HashFileAsync signature anchor not found')
s = s.replace(old_hash_sig, new_hash_sig, 1)
old_hash_update = '''                if (read == 0) break;
                hasher.Update(buffer.AsSpan(0, read));
'''
new_hash_update = '''                if (read == 0) break;
                if (resources is null)
                {
                    hasher.Update(buffer.AsSpan(0, read));
                }
                else
                {
                    using var lease = await resources.EnterCpuWorkAsync(token).ConfigureAwait(false);
                    hasher.Update(buffer.AsSpan(0, read));
                }
'''
hash_index = s.index(new_hash_sig)
tail = s[hash_index:]
if old_hash_update not in tail:
    raise SystemExit('HashFileAsync update anchor not found')
tail = tail.replace(old_hash_update, new_hash_update, 1)
s = s[:hash_index] + tail

# Queue-depth ownership errors must be visible instead of silently clamped.
old_decrement = '''        public void DecrementQueueDepth()
        {
            var depth = Interlocked.Decrement(ref _queueDepth);
            if (depth < 0)
            {
                Interlocked.Exchange(ref _queueDepth, 0);
                depth = 0;
            }
            Progress.SetQueueDepth(depth);
            PulseQueueDrained();
        }
'''
new_decrement = '''        public void DecrementQueueDepth()
        {
            var depth = Interlocked.Decrement(ref _queueDepth);
            if (depth < 0)
            {
                Interlocked.Exchange(ref _queueDepth, 0);
                throw new InvalidOperationException("La profundidad de cola del destino quedó negativa.");
            }
            Progress.SetQueueDepth(depth);
            PulseQueueDrained();
        }
'''
if old_decrement not in s:
    raise SystemExit('queue decrement anchor not found')
s = s.replace(old_decrement, new_decrement, 1)

engine.write_text(s, encoding='utf-8')

# TaskCanceledException is an OperationCanceledException subtype; assert the cancellation contract explicitly.
tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t = tests.read_text(encoding='utf-8')
needle = 'await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await blocked);'
replacement = '''try
        {
            await blocked;
            Assert.Fail("Se esperaba cancelación.");
        }
        catch (OperationCanceledException)
        {
        }'''
t = t.replace(needle, replacement)
tests.write_text(t, encoding='utf-8')
