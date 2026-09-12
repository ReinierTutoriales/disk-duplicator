from pathlib import Path

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')

s = s.replace(
'''    private const int BlockSize = 16 * 1024 * 1024;
    private const int WriteChunkSize = 4 * 1024 * 1024;
    private const int ReservedRam = 512 * 1024 * 1024;
    private const int QueueDepth = 16;
    private const int Retries = 2;
    private const int MaxVerificationParallelism = 8;
''',
'''    private const int BlockSize = 16 * 1024 * 1024;
    private const int WriteChunkSize = 4 * 1024 * 1024;
    private const int SmallBufferSize = 64 * 1024;
    private const int MediumBufferSize = 1024 * 1024;
    private const int LargeBufferSize = 4 * 1024 * 1024;
    private const int PreallocationThreshold = 4 * 1024 * 1024;
    private const int ReservedRam = 512 * 1024 * 1024;
    private const int ChannelCapacity = 16;
    private const int AdaptiveInitialQueue = 4;
    private const int AdaptiveMinQueue = 2;
    private const int AdaptiveMaxQueue = 8;
    private const int FastSamplesToGrow = 8;
    private const int Retries = 2;
    private const int MaxVerificationParallelism = 8;
    private static readonly TimeSpan FastBlockWrite = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan SlowBlockWrite = TimeSpan.FromMilliseconds(250);
''', 1)

s = s.replace('            var queueDepth = QueueDepth;\n', '            var queueDepth = ChannelCapacity;\n', 1)

s = s.replace(
'''                long totalRead = 0;
                while (true)
                {
''',
'''                var readBufferSize = ReadBufferSizeFor(entry.Size);
                long totalRead = 0;
                while (true)
                {
''', 1)
s = s.replace('                    var rented = ArrayPool<byte>.Shared.Rent(BlockSize);\n', '                    var rented = ArrayPool<byte>.Shared.Rent(readBufferSize);\n', 1)
s = s.replace('                        read = await source.ReadAsync(rented.AsMemory(0, BlockSize), token).ConfigureAwait(false);\n', '                        read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), token).ConfigureAwait(false);\n', 1)

start = s.index('    private static async Task DeliverAsync(')
end = s.index('    private static async Task WriterLoopAsync(', start)
new_deliver = '''    private static async Task DeliverAsync(
        IReadOnlyCollection<DestinationWorker> recipients,
        FanoutMessage message,
        bool countsData,
        CopyJob job)
    {
        if (recipients.Count == 0)
            return;

        var deliveries = recipients
            .Select(worker => DeliverOneAsync(worker, message, countsData, job))
            .ToArray();
        await Task.WhenAll(deliveries).ConfigureAwait(false);
    }

    private static async Task DeliverOneAsync(
        DestinationWorker worker,
        FanoutMessage message,
        bool countsData,
        CopyJob job)
    {
        if (!worker.IsActive)
        {
            ReleaseIfData(message);
            return;
        }

        job.Token.ThrowIfCancellationRequested();
        job.WaitIfPaused(job.Token);
        var queued = false;
        try
        {
            if (countsData)
            {
                if (!await worker.WaitForAdaptiveWindowAsync(job.Token).ConfigureAwait(false))
                {
                    ReleaseIfData(message);
                    return;
                }
                worker.IncrementQueueDepth();
                queued = true;
            }

            await worker.Channel.Writer.WriteAsync(message, job.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (queued) worker.DecrementQueueDepth();
            ReleaseIfData(message);
            throw;
        }
        catch (ChannelClosedException)
        {
            if (queued) worker.DecrementQueueDepth();
            ReleaseIfData(message);
            if (worker.IsActive)
                worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
        }
        catch
        {
            if (queued) worker.DecrementQueueDepth();
            ReleaseIfData(message);
            throw;
        }
    }

'''
s = s[:start] + new_deliver + s[end:]

s = s.replace(
'''                                await WriteWithRetryAsync(worker, current, chunkData.Block.Memory, job).ConfigureAwait(false);
                                current.Copied += chunkData.Block.Length;
                                worker.Progress.AddWritten(chunkData.Block.Length);
''',
'''                                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                                await WriteWithRetryAsync(worker, current, chunkData.Block.Memory, job).ConfigureAwait(false);
                                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                                if (chunkData.Block.Length >= WriteChunkSize)
                                    worker.RecordBlockWrite(elapsed);
                                current.Copied += chunkData.Block.Length;
                                worker.Progress.AddWritten(chunkData.Block.Length);
''', 1)

s = s.replace('            PreallocationSize = entry.Size,\n', '            PreallocationSize = entry.Size >= PreallocationThreshold ? entry.Size : 0,\n', 1)

anchor = '''    private static long ToUnixNanoseconds(DateTime utc) =>
        checked((utc.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks) * 100L);

'''
insert = '''    private static int ReadBufferSizeFor(long fileSize) =>
        fileSize <= SmallBufferSize ? SmallBufferSize :
        fileSize <= MediumBufferSize ? MediumBufferSize :
        fileSize <= LargeBufferSize ? LargeBufferSize :
        BlockSize;

'''
if anchor not in s:
    raise SystemExit('buffer sizing anchor not found')
s = s.replace(anchor, anchor + insert, 1)

old = '''    private static void ReleaseUndeliveredData(FanoutMessage message, int count)
    {
        if (message is not DataMessage data) return;
        for (var index = 0; index < count; index++)
            data.Block.Release();
    }

'''
s = s.replace(old, '')

old_worker = '''    private sealed class DestinationWorker
    {
        private int _active = 1;
        private int _queueDepth;
        private long _lastProgressTicks = DateTime.UtcNow.Ticks;

        public DestinationWorker(string root, int slot, DestinationProgress progress, int capacity)
        {
            Root = root;
            Slot = slot;
            Progress = progress;
            Channel = System.Threading.Channels.Channel.CreateBounded<FanoutMessage>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
        }

        public string Root { get; }
        public int Slot { get; }
        public DestinationProgress Progress { get; }
        public Channel<FanoutMessage> Channel { get; }
        public bool IsActive => Volatile.Read(ref _active) != 0;
        public DateTime LastProgressUtc => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);

        public void NoteProgress() => Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);

        public void IncrementQueueDepth()
        {
            var depth = Interlocked.Increment(ref _queueDepth);
            Progress.SetQueueDepth(depth);
        }

        public void DecrementQueueDepth()
        {
            var depth = Math.Max(0, Interlocked.Decrement(ref _queueDepth));
            Progress.SetQueueDepth(depth);
        }

        public void Fail(string error)
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            Progress.MarkError(error);
            Progress.SetPhase(DestinationPhase.Failed, error);
            Channel.Writer.TryComplete();
        }
    }
'''
new_worker = '''    private sealed class DestinationWorker
    {
        private readonly SemaphoreSlim _queueDrained = new(0, 1);
        private int _active = 1;
        private int _queueDepth;
        private int _adaptiveQueueLimit = AdaptiveInitialQueue;
        private int _fastSamples;
        private long _lastProgressTicks = DateTime.UtcNow.Ticks;

        public DestinationWorker(string root, int slot, DestinationProgress progress, int capacity)
        {
            Root = root;
            Slot = slot;
            Progress = progress;
            Channel = System.Threading.Channels.Channel.CreateBounded<FanoutMessage>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        }

        public string Root { get; }
        public int Slot { get; }
        public DestinationProgress Progress { get; }
        public Channel<FanoutMessage> Channel { get; }
        public bool IsActive => Volatile.Read(ref _active) != 0;
        public DateTime LastProgressUtc => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);
        private int QueueDepth => Math.Max(0, Volatile.Read(ref _queueDepth));
        private int AdaptiveQueueLimit => Volatile.Read(ref _adaptiveQueueLimit);

        public void NoteProgress() => Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);

        public async ValueTask<bool> WaitForAdaptiveWindowAsync(CancellationToken token)
        {
            while (IsActive && QueueDepth >= AdaptiveQueueLimit)
                await _queueDrained.WaitAsync(token).ConfigureAwait(false);
            return IsActive;
        }

        public void RecordBlockWrite(TimeSpan elapsed)
        {
            if (elapsed >= SlowBlockWrite)
            {
                Interlocked.Exchange(ref _fastSamples, 0);
                AdjustQueueLimit(-1);
                return;
            }

            if (elapsed <= FastBlockWrite)
            {
                if (Interlocked.Increment(ref _fastSamples) >= FastSamplesToGrow)
                {
                    Interlocked.Exchange(ref _fastSamples, 0);
                    AdjustQueueLimit(1);
                }
                return;
            }

            Interlocked.Exchange(ref _fastSamples, 0);
        }

        public void IncrementQueueDepth()
        {
            var depth = Interlocked.Increment(ref _queueDepth);
            Progress.SetQueueDepth(depth);
        }

        public void DecrementQueueDepth()
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

        public void Fail(string error)
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            Progress.MarkError(error);
            Progress.SetPhase(DestinationPhase.Failed, error);
            Channel.Writer.TryComplete();
            PulseQueueDrained();
        }

        private void AdjustQueueLimit(int delta)
        {
            while (true)
            {
                var current = Volatile.Read(ref _adaptiveQueueLimit);
                var next = Math.Clamp(current + delta, AdaptiveMinQueue, AdaptiveMaxQueue);
                if (next == current)
                    return;
                if (Interlocked.CompareExchange(ref _adaptiveQueueLimit, next, current) == current)
                {
                    if (next > current)
                        PulseQueueDrained();
                    return;
                }
            }
        }

        private void PulseQueueDrained()
        {
            if (_queueDrained.CurrentCount != 0)
                return;
            try { _queueDrained.Release(); }
            catch (SemaphoreFullException) { }
        }
    }
'''
if old_worker not in s:
    raise SystemExit('DestinationWorker anchor not found')
s = s.replace(old_worker, new_worker, 1)

engine.write_text(s, encoding='utf-8')

tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]
    public async Task SingleFileCopiesOnlyThatFile()
'''
new_test = '''    [TestMethod]
    public async Task FanOutHandlesDenseSmallFileTreeAcrossFourDestinations()
    {
        using var temp = new TempDirectory("fanout-small-files");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < 256; index++)
        {
            var relative = Path.Combine($"d{index % 8}", $"f{index:D4}.bin");
            var path = Path.Combine(source, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = new byte[1 + (index * 997) % (48 * 1024)];
            new Random(10_000 + index).NextBytes(payload);
            await File.WriteAllBytesAsync(path, payload);
            expected[relative] = payload;
        }

        var destinations = Enumerable.Range(0, 4)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();
        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        AssertHealthy(job);

        foreach (var destination in destinations)
        {
            var root = Path.Combine(destination, "Origen");
            foreach (var (relative, payload) in expected)
                CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(root, relative)));
        }
    }

'''
if anchor not in t:
    raise SystemExit('test insertion anchor not found')
t = t.replace(anchor, new_test + anchor, 1)
tests.write_text(t, encoding='utf-8')
