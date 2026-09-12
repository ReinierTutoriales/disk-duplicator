from pathlib import Path

p = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = p.read_text(encoding='utf-8')

s = s.replace('    private const int BlockSize = 16 * 1024 * 1024;\n', '    private const int BlockSize = 16 * 1024 * 1024;\n    private const int WriteChunkSize = 4 * 1024 * 1024;\n    private const int QueueDepth = 8;\n', 1)
s = s.replace('    private const int MinQueue = 2;\n    private const int MaxQueue = 16;\n', '')

s = s.replace('            var queueDepth = QueueDepthFor(copy.DestinationRoots.Length);\n', '            var queueDepth = QueueDepth;\n', 1)

# Large sequential I/O is already explicitly buffered by the 16 MiB pooled blocks.
s = s.replace('                    BufferSize = 1024 * 1024,\n', '                    BufferSize = 1,\n', 1)

old = '''        var stream = new FileStream(part, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1024 * 1024,
        });
'''
new = '''        var stream = new FileStream(part, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1,
            PreallocationSize = entry.Size,
        });
'''
if old not in s:
    raise SystemExit('BeginFile stream anchor not found')
s = s.replace(old, new, 1)

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

# Verification hashing uses its own 4 MiB rented buffer, so disable FileStream's extra buffer there too.
s = s.replace('                BufferSize = 1024 * 1024,\n', '                BufferSize = 1,\n', 1)

old = '''            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1024 * 1024,
        });
'''
new = '''            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1,
        });
'''
if old not in s:
    raise SystemExit('ReopenPart stream anchor not found')
s = s.replace(old, new, 1)

old = '''    private static int QueueDepthFor(int destinations) =>
        destinations <= 0
            ? MinQueue
            : Math.Clamp(ReservedRam / (destinations * BlockSize), MinQueue, MaxQueue);

'''
s = s.replace(old, '')

p.write_text(s, encoding='utf-8')
