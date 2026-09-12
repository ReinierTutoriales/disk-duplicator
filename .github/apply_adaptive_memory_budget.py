from pathlib import Path

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')

s = s.replace('using System.Threading.Channels;\n', 'using System.Threading.Channels;\nusing System.Runtime.InteropServices;\n', 1)

s = s.replace('    private const int ReservedRam = 512 * 1024 * 1024;\n', '''    private const long MinimumBufferBudget = 256L * 1024 * 1024;\n    private const long InitialBufferBudget = 512L * 1024 * 1024;\n    private const long MaximumBufferBudget = 4L * 1024 * 1024 * 1024;\n    private const long BufferBudgetGrowthStep = 256L * 1024 * 1024;\n''', 1)

s = s.replace('        var bufferBudget = new SemaphoreSlim(Math.Max(8, ReservedRam / BlockSize));\n', '        var bufferBudget = AdaptiveByteBudget.CreateForSystem();\n', 1)

s = s.replace('        SemaphoreSlim bufferBudget,\n', '        AdaptiveByteBudget bufferBudget,\n')
s = s.replace('        SemaphoreSlim bufferBudget,\n', '        AdaptiveByteBudget bufferBudget,\n')

s = s.replace('            await bufferBudget.WaitAsync(job.Token).ConfigureAwait(false);\n', '            await bufferBudget.AcquireAsync(readBufferSize, job.Token).ConfigureAwait(false);\n')
s = s.replace('                bufferBudget.Release();\n', '                bufferBudget.Release(readBufferSize);\n')

# Prefetch path uses its cancellation token rather than job.Token.
s = s.replace('                await bufferBudget.WaitAsync(token).ConfigureAwait(false);\n', '                await bufferBudget.AcquireAsync(readBufferSize, token).ConfigureAwait(false);\n')
s = s.replace('                    bufferBudget.Release();\n', '                    bufferBudget.Release(readBufferSize);\n')

# Shared/source blocks now carry exact reserved bytes.
s = s.replace('                var block = new SourceReadBlock(rented, read, bufferBudget);\n', '                var block = new SourceReadBlock(rented, read, readBufferSize, bufferBudget);\n')
s = s.replace('            var block = new SharedBlock(rented, read, recipients.Length, bufferBudget);\n', '            var block = new SharedBlock(rented, read, readBufferSize, recipients.Length, bufferBudget);\n')

old_source_block = '''    private sealed class SourceReadBlock\n    {\n        private byte[]? _buffer;\n        private readonly SemaphoreSlim _budget;\n\n        public SourceReadBlock(byte[] buffer, int length, SemaphoreSlim budget)\n        {\n            _buffer = buffer;\n            Length = length;\n            _budget = budget;\n        }\n'''
new_source_block = '''    private sealed class SourceReadBlock\n    {\n        private byte[]? _buffer;\n        private readonly int _reservedBytes;\n        private readonly AdaptiveByteBudget _budget;\n\n        public SourceReadBlock(byte[] buffer, int length, int reservedBytes, AdaptiveByteBudget budget)\n        {\n            _buffer = buffer;\n            Length = length;\n            _reservedBytes = reservedBytes;\n            _budget = budget;\n        }\n'''
if old_source_block not in s:
    raise SystemExit('SourceReadBlock anchor not found')
s = s.replace(old_source_block, new_source_block, 1)
s = s.replace('            return new SharedBlock(buffer, Length, references, _budget);\n', '            return new SharedBlock(buffer, Length, _reservedBytes, references, _budget);\n', 1)
s = s.replace('            _budget.Release();\n', '            _budget.Release(_reservedBytes);\n', 1)

old_shared = '''    private sealed class SharedBlock\n    {\n        private byte[]? _buffer;\n        private int _references;\n        private readonly SemaphoreSlim _budget;\n\n        public SharedBlock(byte[] buffer, int length, int references, SemaphoreSlim budget)\n        {\n            _buffer = buffer;\n            Length = length;\n            _references = references;\n            _budget = budget;\n        }\n'''
new_shared = '''    private sealed class SharedBlock\n    {\n        private byte[]? _buffer;\n        private int _references;\n        private readonly int _reservedBytes;\n        private readonly AdaptiveByteBudget _budget;\n\n        public SharedBlock(byte[] buffer, int length, int reservedBytes, int references, AdaptiveByteBudget budget)\n        {\n            _buffer = buffer;\n            Length = length;\n            _reservedBytes = reservedBytes;\n            _references = references;\n            _budget = budget;\n        }\n'''
if old_shared not in s:
    raise SystemExit('SharedBlock anchor not found')
s = s.replace(old_shared, new_shared, 1)
# Replace the SharedBlock release; SourceReadBlock already changed first occurrence.
idx = s.index('    private sealed class SharedBlock')
tail = s[idx:]
tail = tail.replace('            _budget.Release();\n', '            _budget.Release(_reservedBytes);\n', 1)
s = s[:idx] + tail

# Add adaptive byte budget before DestinationWorker.
anchor = '    private sealed class DestinationWorker\n'
idx = s.find(anchor)
if idx < 0:
    raise SystemExit('DestinationWorker anchor not found')

budget_class = r'''    private sealed class AdaptiveByteBudget
    {
        private readonly object _gate = new();
        private readonly Queue<Waiter> _waiters = new();
        private readonly long _maximumBytes;
        private long _targetBytes;
        private long _usedBytes;

        private AdaptiveByteBudget(long initialBytes, long maximumBytes)
        {
            _targetBytes = initialBytes;
            _maximumBytes = maximumBytes;
        }

        public static AdaptiveByteBudget CreateForSystem()
        {
            var memory = GetMemoryStatus();
            var total = checked((long)Math.Min(memory.ullTotalPhys, (ulong)long.MaxValue));
            var available = checked((long)Math.Min(memory.ullAvailPhys, (ulong)long.MaxValue));
            var maximum = Math.Clamp(total / 8, MinimumBufferBudget, MaximumBufferBudget);
            var reserve = Math.Max(2L * 1024 * 1024 * 1024, total / 4);
            var safeNow = Math.Max(MinimumBufferBudget, available - reserve);
            maximum = Math.Max(MinimumBufferBudget, Math.Min(maximum, safeNow));
            var initial = Math.Min(InitialBufferBudget, maximum);
            return new AdaptiveByteBudget(initial, maximum);
        }

        public ValueTask AcquireAsync(int bytes, CancellationToken token)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            lock (_gate)
            {
                if (_waiters.Count == 0 && TryAcquireLocked(bytes))
                    return ValueTask.CompletedTask;

                var waiter = new Waiter(bytes);
                _waiters.Enqueue(waiter);
                return new ValueTask(WaitAsync(waiter, token));
            }
        }

        public void Release(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            List<Waiter>? ready = null;
            lock (_gate)
            {
                _usedBytes = Math.Max(0, _usedBytes - bytes);
                ready = PumpWaitersLocked();
            }
            Complete(ready);
        }

        private async Task WaitAsync(Waiter waiter, CancellationToken token)
        {
            try
            {
                await waiter.Completion.Task.WaitAsync(token).ConfigureAwait(false);
            }
            catch
            {
                List<Waiter>? ready = null;
                lock (_gate)
                {
                    waiter.Cancelled = true;
                    ready = PumpWaitersLocked();
                }
                Complete(ready);
                throw;
            }
        }

        private bool TryAcquireLocked(int bytes)
        {
            if (_usedBytes + bytes <= _targetBytes)
            {
                _usedBytes += bytes;
                return true;
            }

            if (TryGrowLocked(bytes) && _usedBytes + bytes <= _targetBytes)
            {
                _usedBytes += bytes;
                return true;
            }
            return false;
        }

        private bool TryGrowLocked(int bytes)
        {
            if (_targetBytes >= _maximumBytes)
                return false;

            var memory = GetMemoryStatus();
            var available = checked((long)Math.Min(memory.ullAvailPhys, (ulong)long.MaxValue));
            var total = checked((long)Math.Min(memory.ullTotalPhys, (ulong)long.MaxValue));
            var reserve = Math.Max(2L * 1024 * 1024 * 1024, total / 4);
            var headroom = available - reserve;
            if (headroom < BufferBudgetGrowthStep)
                return false;

            var requestedTarget = Math.Max(_targetBytes + BufferBudgetGrowthStep, _usedBytes + bytes);
            var safeTarget = _usedBytes + headroom;
            var next = Math.Min(_maximumBytes, Math.Min(requestedTarget, safeTarget));
            if (next <= _targetBytes)
                return false;
            _targetBytes = next;
            return true;
        }

        private List<Waiter>? PumpWaitersLocked()
        {
            List<Waiter>? ready = null;
            while (_waiters.Count > 0)
            {
                var waiter = _waiters.Peek();
                if (waiter.Cancelled)
                {
                    _waiters.Dequeue();
                    continue;
                }
                if (!TryAcquireLocked(waiter.Bytes))
                    break;
                _waiters.Dequeue();
                (ready ??= []).Add(waiter);
            }
            return ready;
        }

        private static void Complete(List<Waiter>? ready)
        {
            if (ready is null)
                return;
            foreach (var waiter in ready)
                waiter.Completion.TrySetResult();
        }

        private static MemoryStatusEx GetMemoryStatus()
        {
            var status = new MemoryStatusEx
            {
                dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>(),
            };
            if (!GlobalMemoryStatusEx(ref status))
                throw new IOException($"No se pudo consultar la memoria física de Windows: {Marshal.GetLastWin32Error()}.");
            return status;
        }

        private sealed class Waiter(int bytes)
        {
            public int Bytes { get; } = bytes;
            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Cancelled { get; set; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
    }

'''
s = s[:idx] + budget_class + s[idx:]

engine.write_text(s, encoding='utf-8')

# Add a regression that mixes sub-MiB, 4 MiB and 16+ MiB buffers across multiple destinations.
tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]\n    public async Task FanOutPrefetchPreservesOrderingAcrossThreeLargeDestinations()\n'''
if anchor not in t:
    raise SystemExit('test insertion anchor not found')
new_test = '''    [TestMethod]\n    public async Task FanOutMixedBufferSizesRemainExactAcrossFiveDestinations()\n    {\n        using var temp = new TempDirectory("fanout-mixed-byte-budget");\n        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;\n        var files = new Dictionary<string, byte[]>\n        {\n            ["tiny.bin"] = new byte[31 * 1024 + 7],\n            ["medium.bin"] = new byte[900 * 1024 + 13],\n            ["large.bin"] = new byte[3 * 1024 * 1024 + 29],\n            ["prefetch.bin"] = new byte[20 * 1024 * 1024 + 41],\n        };\n        var seed = 7000;\n        foreach (var (name, payload) in files)\n        {\n            new Random(seed++).NextBytes(payload);\n            await File.WriteAllBytesAsync(Path.Combine(source, name), payload);\n        }\n\n        var destinations = Enumerable.Range(0, 5)\n            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)\n            .ToArray();\n        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);\n        await using var job = CopyEngine.Start(plan);\n        await job.Completion.WaitAsync(TimeSpan.FromSeconds(45));\n        AssertHealthy(job);\n\n        foreach (var destination in destinations)\n        {\n            var root = Path.Combine(destination, "Origen");\n            foreach (var (name, payload) in files)\n                CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(root, name)));\n        }\n    }\n\n'''
t = t.replace(anchor, new_test + anchor, 1)
tests.write_text(t, encoding='utf-8')
