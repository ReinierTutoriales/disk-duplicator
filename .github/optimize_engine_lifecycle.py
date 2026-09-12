from pathlib import Path

p = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = p.read_text(encoding='utf-8')

s = s.replace('    private const int MaxPendingPerDestination = 32;\n', '')
s = s.replace('    private static readonly TimeSpan WriteStallThreshold = TimeSpan.FromSeconds(30);\n', '')

old = '''            var writerTasks = workers
                .Select(worker => Task.Run(() => WriterLoopAsync(worker, options, job, expectedHashes), CancellationToken.None))
                .ToArray();

            await ProducerLoopAsync(copy, workers, progress, skipMasks, expectedHashes, job).ConfigureAwait(false);
            foreach (var worker in workers) worker.Channel.Writer.TryComplete();
            await Task.WhenAll(writerTasks).ConfigureAwait(false);

            if (options.Verify && !token.IsCancellationRequested)
'''
new = '''            var writerTasks = workers
                .Select(worker => Task.Run(() => WriterLoopAsync(worker, options, job, expectedHashes), CancellationToken.None))
                .ToArray();

            Exception? producerError = null;
            try
            {
                await ProducerLoopAsync(copy, workers, progress, skipMasks, expectedHashes, job).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                producerError = ex;
            }
            finally
            {
                foreach (var worker in workers)
                    worker.Channel.Writer.TryComplete(producerError);
            }

            Exception? writerError = null;
            try
            {
                await Task.WhenAll(writerTasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                writerError = ex;
            }

            if (producerError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producerError).Throw();
            if (writerError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writerError).Throw();

            if (options.Verify && !token.IsCancellationRequested)
'''
if old not in s:
    raise SystemExit('run lifecycle anchor not found')
s = s.replace(old, new, 1)

old = '''        finally
        {
            foreach (var worker in workers)
            {
                worker.Channel.Writer.TryComplete();
                DrainAndRelease(worker);
            }
        }
'''
new = '''        finally
        {
            foreach (var worker in workers)
            {
                worker.Channel.Writer.TryComplete();
                DrainAndRelease(worker);
            }
        }
'''
# Intentionally unchanged: by this point writer tasks are always joined above.
if old not in s:
    raise SystemExit('final cleanup anchor not found')

start = s.index('    private static async Task DeliverAsync(')
end = s.index('    private static async Task WriterLoopAsync(', start)
new_deliver = '''    private static async Task DeliverAsync(
        IReadOnlyCollection<DestinationWorker> recipients,
        FanoutMessage message,
        bool countsData,
        CopyJob job)
    {
        var targets = recipients as DestinationWorker[] ?? recipients.ToArray();
        for (var index = 0; index < targets.Length; index++)
        {
            var worker = targets[index];
            if (!worker.IsActive)
            {
                ReleaseIfData(message);
                continue;
            }

            job.Token.ThrowIfCancellationRequested();
            job.WaitIfPaused(job.Token);
            if (countsData) worker.IncrementQueueDepth();
            try
            {
                await worker.Channel.Writer.WriteAsync(message, job.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (countsData) worker.DecrementQueueDepth();
                ReleaseIfData(message);
                ReleaseUndeliveredData(message, targets.Length - index - 1);
                throw;
            }
            catch (ChannelClosedException)
            {
                if (countsData) worker.DecrementQueueDepth();
                ReleaseIfData(message);
                if (worker.IsActive)
                    worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
            }
            catch
            {
                if (countsData) worker.DecrementQueueDepth();
                ReleaseIfData(message);
                ReleaseUndeliveredData(message, targets.Length - index - 1);
                throw;
            }
        }
    }

'''
s = s[:start] + new_deliver + s[end:]

old = '''    private static bool IsInside(string parent, string candidate)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)) + Path.DirectorySeparatorChar;
        return target.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

'''
s = s.replace(old, '')

start = s.index('    private static void FlushPending(DestinationWorker worker)')
end = s.index('    private static void DrainAndRelease(DestinationWorker worker)', start)
s = s[:start] + s[end:]

old = '''    private static void DrainAndRelease(DestinationWorker worker)
    {
        DropPending(worker);
        while (worker.Channel.Reader.TryRead(out var message))
'''
new = '''    private static void DrainAndRelease(DestinationWorker worker)
    {
        while (worker.Channel.Reader.TryRead(out var message))
'''
if old not in s:
    raise SystemExit('drain anchor not found')
s = s.replace(old, new, 1)

old = '''    private static void ReleaseIfData(FanoutMessage message)
    {
        if (message is DataMessage data) data.Block.Release();
    }

'''
new = '''    private static void ReleaseIfData(FanoutMessage message)
    {
        if (message is DataMessage data) data.Block.Release();
    }

    private static void ReleaseUndeliveredData(FanoutMessage message, int count)
    {
        if (message is not DataMessage data) return;
        for (var index = 0; index < count; index++)
            data.Block.Release();
    }

'''
if old not in s:
    raise SystemExit('release helper anchor not found')
s = s.replace(old, new, 1)

old = '''    {
        public static FileEntry From(string sourceRoot, string sourcePath)
        {
            var info = new FileInfo(sourcePath);
            var modified = info.LastWriteTimeUtc;
            return new FileEntry(
                sourcePath,
                Path.GetRelativePath(sourceRoot, sourcePath),
                info.Length,
                modified,
                ToUnixNanoseconds(modified));
        }
    }
'''
new = '''    ;
'''
if old not in s:
    raise SystemExit('FileEntry.From anchor not found')
s = s.replace(old, new, 1)

s = s.replace('        public ConcurrentQueue<FanoutMessage> Pending { get; } = new();\n', '')

# Avoid double-counting one file failure when KeepGoing is disabled.
s = s.replace('''                            worker.Progress.MarkError(ex.Message);
                            if (!options.KeepGoing) worker.Fail(ex.Message);
''', '''                            if (options.KeepGoing) worker.Progress.MarkError(ex.Message);
                            else worker.Fail(ex.Message);
''', 1)
s = s.replace('''            worker.Progress.MarkError($"Tamaño inesperado en {current.Entry.RelativePath}");
            if (!options.KeepGoing) worker.Fail("Tamaño inesperado en temporal.");
''', '''            var error = $"Tamaño inesperado en {current.Entry.RelativePath}";
            if (options.KeepGoing) worker.Progress.MarkError(error);
            else worker.Fail(error);
''', 1)

p.write_text(s, encoding='utf-8')
