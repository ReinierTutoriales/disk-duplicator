from pathlib import Path

p = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = p.read_text(encoding='utf-8')

if 'private const int WriteChunkSize' not in s:
    anchor = '    private const int BlockSize = 16 * 1024 * 1024;\n'
    if anchor not in s:
        raise SystemExit('BlockSize anchor not found')
    s = s.replace(anchor, anchor + '    private const int WriteChunkSize = 4 * 1024 * 1024;\n', 1)

old = '''                current.Stream ??= ReopenPart(current.PartPath, current.Copied);
                await current.Stream.WriteAsync(data, job.Token).ConfigureAwait(false);
                return;
'''
new = '''                current.Stream ??= ReopenPart(current.PartPath, current.Copied);
                var remaining = data;
                while (!remaining.IsEmpty)
                {
                    var length = Math.Min(WriteChunkSize, remaining.Length);
                    await current.Stream.WriteAsync(remaining[..length], job.Token).ConfigureAwait(false);
                    remaining = remaining[length..];
                    worker.NoteProgress();
                }
                return;
'''
if old not in s:
    raise SystemExit('WriteWithRetry anchor not found')
s = s.replace(old, new, 1)

p.write_text(s, encoding='utf-8')
