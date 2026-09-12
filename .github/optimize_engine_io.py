from pathlib import Path

p = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = p.read_text(encoding='utf-8')

s = s.replace('    private const int MinQueue = 2;\n    private const int MaxQueue = 16;\n', '    private const int QueueDepth = 16;\n')
s = s.replace('            var queueDepth = QueueDepthFor(copy.DestinationRoots.Length);\n', '            var queueDepth = QueueDepth;\n')

# Our FAN-OUT blocks are already large and pooled. Disable FileStream's additional
# managed buffer so each async call maps directly to the intended overlapped I/O.
s = s.replace('                    BufferSize = 1024 * 1024,\n', '                    BufferSize = 1,\n', 1)

old = '''            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1024 * 1024,
        });
        return new CurrentFile(entry, destination, part, stream);
'''
new = '''            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1,
            PreallocationSize = entry.Size,
        });
        return new CurrentFile(entry, destination, part, stream);
'''
if old not in s:
    raise SystemExit('destination stream anchor not found')
s = s.replace(old, new, 1)

# Hashing uses its own 4 MiB ArrayPool buffer; a second FileStream buffer is redundant.
old = '''                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 1024 * 1024,
            });
            while (true)
'''
new = '''                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 1,
            });
            while (true)
'''
if old not in s:
    raise SystemExit('hash stream anchor not found')
s = s.replace(old, new, 1)

old = '''    private static int QueueDepthFor(int destinations) =>
        destinations <= 0
            ? MinQueue
            : Math.Clamp(ReservedRam / (destinations * BlockSize), MinQueue, MaxQueue);

'''
if old not in s:
    raise SystemExit('queue depth method anchor not found')
s = s.replace(old, '')

old = '''            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1024 * 1024,
        });
        stream.Position = offset;
'''
new = '''            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1,
        });
        stream.Position = offset;
'''
if old not in s:
    raise SystemExit('reopen stream anchor not found')
s = s.replace(old, new, 1)

# Clean record declaration left from the previous dead-code removal.
s = s.replace('''    private sealed record FileEntry(
        string SourcePath,
        string RelativePath,
        long Size,
        DateTime LastWriteTimeUtc,
        long ModifiedUnixNanoseconds)
    ;
''', '''    private sealed record FileEntry(
        string SourcePath,
        string RelativePath,
        long Size,
        DateTime LastWriteTimeUtc,
        long ModifiedUnixNanoseconds);
''')

p.write_text(s, encoding='utf-8')
