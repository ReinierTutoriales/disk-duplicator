from pathlib import Path

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')

const_anchor = '    private const int BlockSize = 16 * 1024 * 1024;\n'
if const_anchor not in s:
    raise SystemExit('BlockSize anchor not found')
s = s.replace(
    const_anchor,
    const_anchor + '    private const int SourcePrefetchDepth = 2;\n    private const int SourcePrefetchThreshold = 16 * 1024 * 1024;\n',
    1)

old = '''                using var hasher = Hasher.New();
                await using var source = new FileStream(entry.SourcePath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    BufferSize = 1,
                });

                var readBufferSize = ReadBufferSizeFor(entry.Size);
                long totalRead = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    job.WaitIfPaused(token);
                    await bufferBudget.WaitAsync(token).ConfigureAwait(false);
                    var rented = ArrayPool<byte>.Shared.Rent(readBufferSize);
                    int read;
                    try
                    {
                        read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), token).ConfigureAwait(false);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                        bufferBudget.Release();
                        throw;
                    }

                    if (read == 0)
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                        bufferBudget.Release();
                        break;
                    }

                    totalRead += read;
                    hasher.Update(rented.AsSpan(0, read));
                    var recipients = active.Where(worker => worker.IsActive).ToArray();
                    if (recipients.Length == 0)
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                        bufferBudget.Release();
                        break;
                    }

                    var block = new SharedBlock(rented, read, recipients.Length, bufferBudget);
                    await DeliverAsync(recipients, new DataMessage(block), countsData: true, job).ConfigureAwait(false);
                    active.RemoveAll(worker => !worker.IsActive);
                    if (active.Count == 0) break;
                }

                if (totalRead != entry.Size)
                    throw new IOException($"El origen cambió de tamaño durante la copia: {entry.RelativePath}");
                ValidateSourceSnapshot(entry);
                var hash = hasher.Finalize().AsSpan().ToArray();
'''
new = '''                var sourceResult = entry.Size >= SourcePrefetchThreshold
                    ? await ReadAndFanOutPrefetchedAsync(entry, active, bufferBudget, job).ConfigureAwait(false)
                    : await ReadAndFanOutSequentialAsync(entry, active, bufferBudget, job).ConfigureAwait(false);
                if (sourceResult is null)
                    continue;

                var hash = sourceResult.Hash;
'''
if old not in s:
    raise SystemExit('producer source loop anchor not found')
s = s.replace(old, new, 1)

insert_anchor = '    private static async Task DeliverAsync(\n'
idx = s.find(insert_anchor)
if idx < 0:
    raise SystemExit('DeliverAsync insertion anchor not found')

helpers = r'''    private static async Task<SourceReadResult?> ReadAndFanOutSequentialAsync(
        FileEntry entry,
        List<DestinationWorker> active,
        SemaphoreSlim bufferBudget,
        CopyJob job)
    {
        using var hasher = Hasher.New();
        await using var source = OpenSourceStream(entry.SourcePath);
        var readBufferSize = ReadBufferSizeFor(entry.Size);
        long totalRead = 0;

        while (true)
        {
            job.Token.ThrowIfCancellationRequested();
            job.WaitIfPaused(job.Token);
            await bufferBudget.WaitAsync(job.Token).ConfigureAwait(false);
            var rented = ArrayPool<byte>.Shared.Rent(readBufferSize);
            int read;
            try
            {
                read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), job.Token).ConfigureAwait(false);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(rented);
                bufferBudget.Release();
                throw;
            }

            if (read == 0)
            {
                ArrayPool<byte>.Shared.Return(rented);
                bufferBudget.Release();
                break;
            }

            totalRead += read;
            hasher.Update(rented.AsSpan(0, read));
            var recipients = active.Where(worker => worker.IsActive).ToArray();
            if (recipients.Length == 0)
            {
                ArrayPool<byte>.Shared.Return(rented);
                bufferBudget.Release();
                return null;
            }

            var block = new SharedBlock(rented, read, recipients.Length, bufferBudget);
            await DeliverAsync(recipients, new DataMessage(block), countsData: true, job).ConfigureAwait(false);
            active.RemoveAll(worker => !worker.IsActive);
            if (active.Count == 0)
                return null;
        }

        ValidateCompletedSourceRead(entry, totalRead);
        return new SourceReadResult(totalRead, hasher.Finalize().AsSpan().ToArray());
    }

    private static async Task<SourceReadResult?> ReadAndFanOutPrefetchedAsync(
        FileEntry entry,
        List<DestinationWorker> active,
        SemaphoreSlim bufferBudget,
        CopyJob job)
    {
        using var prefetchCancel = CancellationTokenSource.CreateLinkedTokenSource(job.Token);
        var sourceQueue = Channel.CreateBounded<SourceReadBlock>(new BoundedChannelOptions(SourcePrefetchDepth)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var readTask = PrefetchSourceAsync(
            entry,
            ReadBufferSizeFor(entry.Size),
            sourceQueue.Writer,
            bufferBudget,
            job,
            prefetchCancel.Token);

        Exception? deliveryError = null;
        var stoppedEarly = false;
        try
        {
            await foreach (var sourceBlock in sourceQueue.Reader.ReadAllAsync(job.Token).ConfigureAwait(false))
            {
                var recipients = active.Where(worker => worker.IsActive).ToArray();
                if (recipients.Length == 0)
                {
                    sourceBlock.Release();
                    stoppedEarly = true;
                    prefetchCancel.Cancel();
                    break;
                }

                var shared = sourceBlock.TransferToShared(recipients.Length);
                await DeliverAsync(recipients, new DataMessage(shared), countsData: true, job).ConfigureAwait(false);
                active.RemoveAll(worker => !worker.IsActive);
                if (active.Count == 0)
                {
                    stoppedEarly = true;
                    prefetchCancel.Cancel();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            deliveryError = ex;
            prefetchCancel.Cancel();
        }

        SourceReadResult? result = null;
        try
        {
            result = await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppedEarly || deliveryError is not null || job.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            deliveryError ??= ex;
        }
        finally
        {
            while (sourceQueue.Reader.TryRead(out var leftover))
                leftover.Release();
        }

        if (deliveryError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(deliveryError).Throw();
        if (stoppedEarly)
            return null;
        return result ?? throw new IOException($"La lectura anticipada terminó sin resultado: {entry.RelativePath}");
    }

    private static async Task<SourceReadResult> PrefetchSourceAsync(
        FileEntry entry,
        int readBufferSize,
        ChannelWriter<SourceReadBlock> output,
        SemaphoreSlim bufferBudget,
        CopyJob job,
        CancellationToken token)
    {
        Exception? completionError = null;
        try
        {
            using var hasher = Hasher.New();
            await using var source = OpenSourceStream(entry.SourcePath);
            long totalRead = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                job.WaitIfPaused(token);
                await bufferBudget.WaitAsync(token).ConfigureAwait(false);
                var rented = ArrayPool<byte>.Shared.Rent(readBufferSize);
                int read;
                try
                {
                    read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), token).ConfigureAwait(false);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(rented);
                    bufferBudget.Release();
                    throw;
                }

                if (read == 0)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                    bufferBudget.Release();
                    break;
                }

                totalRead += read;
                hasher.Update(rented.AsSpan(0, read));
                var block = new SourceReadBlock(rented, read, bufferBudget);
                try
                {
                    await output.WriteAsync(block, token).ConfigureAwait(false);
                }
                catch
                {
                    block.Release();
                    throw;
                }
            }

            ValidateCompletedSourceRead(entry, totalRead);
            return new SourceReadResult(totalRead, hasher.Finalize().AsSpan().ToArray());
        }
        catch (Exception ex)
        {
            completionError = ex;
            throw;
        }
        finally
        {
            output.TryComplete(completionError);
        }
    }

    private static FileStream OpenSourceStream(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1,
        });

    private static void ValidateCompletedSourceRead(FileEntry entry, long totalRead)
    {
        if (totalRead != entry.Size)
            throw new IOException($"El origen cambió de tamaño durante la copia: {entry.RelativePath}");
        ValidateSourceSnapshot(entry);
    }

'''
s = s[:idx] + helpers + s[idx:]

record_anchor = '''    private abstract record FanoutMessage;
'''
if record_anchor not in s:
    raise SystemExit('record anchor not found')
s = s.replace(record_anchor, '''    private sealed record SourceReadResult(long BytesRead, byte[] Hash);

    private sealed class SourceReadBlock
    {
        private byte[]? _buffer;
        private readonly SemaphoreSlim _budget;

        public SourceReadBlock(byte[] buffer, int length, SemaphoreSlim budget)
        {
            _buffer = buffer;
            Length = length;
            _budget = budget;
        }

        public int Length { get; }

        public SharedBlock TransferToShared(int references)
        {
            if (references <= 0)
                throw new ArgumentOutOfRangeException(nameof(references));
            var buffer = Interlocked.Exchange(ref _buffer, null)
                ?? throw new ObjectDisposedException(nameof(SourceReadBlock));
            return new SharedBlock(buffer, Length, references, _budget);
        }

        public void Release()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is null)
                return;
            ArrayPool<byte>.Shared.Return(buffer);
            _budget.Release();
        }
    }

''' + record_anchor, 1)

engine.write_text(s, encoding='utf-8')

tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]
    public async Task FanOutHandlesDenseSmallFileTreeAcrossFourDestinations()
'''
if anchor not in t:
    raise SystemExit('test anchor not found')
new_test = '''    [TestMethod]
    public async Task FanOutPrefetchPreservesOrderingAcrossThreeLargeDestinations()
    {
        using var temp = new TempDirectory("fanout-prefetch-order");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[40 * 1024 * 1024 + 777];
        new Random(54321).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "prefetch.bin"), payload);
        var destinations = Enumerable.Range(0, 3)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();

        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        AssertHealthy(job);

        foreach (var destination in destinations)
            CollectionAssert.AreEqual(
                payload,
                await File.ReadAllBytesAsync(Path.Combine(destination, "Origen", "prefetch.bin")));
    }

'''
t = t.replace(anchor, new_test + anchor, 1)
tests.write_text(t, encoding='utf-8')
