from pathlib import Path

engine = Path("dotnet/RepartoCopier.Core/CopyEngine.cs")
text = engine.read_text(encoding="utf-8")
replacements = [
    ("if (message is DataMessage data && !worker.IsActive)\n                {\n                    worker.DecrementQueueDepth();\n                    data.Block.Release();", "if (message is DataMessage droppedData && !worker.IsActive)\n                {\n                    worker.DecrementQueueDepth();\n                    droppedData.Block.Release();"),
    ("case DataMessage data when current is not null:\n                        try", "case DataMessage chunkData when current is not null:\n                        try"),
    ("await WriteWithRetryAsync(worker, current, data.Block.Memory, job).ConfigureAwait(false);\n                                current.Copied += data.Block.Length;\n                                worker.Progress.AddWritten(data.Block.Length);", "await WriteWithRetryAsync(worker, current, chunkData.Block.Memory, job).ConfigureAwait(false);\n                                current.Copied += chunkData.Block.Length;\n                                worker.Progress.AddWritten(chunkData.Block.Length);"),
    ("worker.DecrementQueueDepth();\n                            data.Block.Release();\n                        }\n                        break;\n                    case DataMessage data:\n                        worker.DecrementQueueDepth();\n                        data.Block.Release();", "worker.DecrementQueueDepth();\n                            chunkData.Block.Release();\n                        }\n                        break;\n                    case DataMessage orphanData:\n                        worker.DecrementQueueDepth();\n                        orphanData.Block.Release();"),
    ("while (worker.Pending.TryPeek(out var message) && worker.Channel.Writer.TryWrite(message))\n            worker.Pending.Dequeue();", "while (worker.Pending.TryPeek(out var message) && worker.Channel.Writer.TryWrite(message))\n            worker.Pending.TryDequeue(out _);")
]
for old, new in replacements:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"expected one compile-fix anchor, found {count}: {old[:80]!r}")
    text = text.replace(old, new, 1)

old_deliver = '''    private static async Task DeliverAsync(
        IReadOnlyCollection<DestinationWorker> recipients,
        FanoutMessage message,
        bool countsData,
        CopyJob job)
    {
        foreach (var worker in recipients)
        {
            if (!worker.IsActive)
            {
                ReleaseIfData(message);
                continue;
            }

            FlushPending(worker);
            while (worker.Pending.Count >= MaxPendingPerDestination)
            {
                job.Token.ThrowIfCancellationRequested();
                job.WaitIfPaused(job.Token);
                FlushPending(worker);
                if (!worker.IsActive)
                {
                    ReleaseIfData(message);
                    break;
                }
                if (DateTime.UtcNow - worker.LastProgressUtc >= WriteStallThreshold)
                {
                    worker.Fail($"Destino atascado: sin progreso durante {WriteStallThreshold.TotalSeconds:0} s.");
                    DropPending(worker);
                    ReleaseIfData(message);
                    break;
                }
                await Task.Delay(2, job.Token).ConfigureAwait(false);
            }

            if (!worker.IsActive) continue;
            if (countsData) worker.IncrementQueueDepth();
            if (worker.Pending.Count > 0 || !worker.Channel.Writer.TryWrite(message))
                worker.Pending.Enqueue(message);
        }
    }
'''
new_deliver = '''    private static async Task DeliverAsync(
        IReadOnlyCollection<DestinationWorker> recipients,
        FanoutMessage message,
        bool countsData,
        CopyJob job)
    {
        foreach (var worker in recipients)
        {
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
                throw;
            }
            catch (ChannelClosedException)
            {
                if (countsData) worker.DecrementQueueDepth();
                ReleaseIfData(message);
                if (worker.IsActive)
                    worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
            }
        }
    }
'''
if text.count(old_deliver) != 1:
    raise SystemExit("expected one DeliverAsync anchor")
text = text.replace(old_deliver, new_deliver, 1)

old_budget_dispose = '''        finally
        {
            bufferBudget.Dispose();
        }
    }

    private static async Task DeliverAsync'''
new_budget_dispose = '''        finally
        {
            // SharedBlock instances can outlive the producer while destination writers
            // drain their bounded channels. Disposing the semaphore here races with the
            // final SharedBlock.Release() calls and can abort otherwise valid copies.
            // The semaphore is intentionally left for GC once the last shared block and
            // this producer scope release their references.
        }
    }

    private static async Task DeliverAsync'''
if text.count(old_budget_dispose) != 1:
    raise SystemExit("expected one buffer budget lifetime anchor")
text = text.replace(old_budget_dispose, new_budget_dispose, 1)
engine.write_text(text, encoding="utf-8")

tests = Path("dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs")
text = tests.read_text(encoding="utf-8")
old = "Assert.ThrowsException<ArgumentException>(() =>\n            CopyPlan.Create(\"C:/Origen\", [\"C:/Origen/\"], true, true));"
new = "Assert.ThrowsExactly<ArgumentException>(() =>\n            CopyPlan.Create(\"C:/Origen\", [\"C:/Origen/\"], true, true));"
if text.count(old) != 1:
    raise SystemExit("expected one MSTest assertion anchor")
text = text.replace(old, new, 1)

anchor = '''    [TestMethod]\n    public async Task FanOutPreservesRootTreeEmptyDirectoriesAndBytes()'''
if anchor not in text:
    raise SystemExit("expected FAN-OUT parity test anchor")
insert = '''    [TestMethod]\n    public async Task FanOutDeliversMultipleBlocksToEveryDestination()\n    {\n        using var temp = new TempDirectory("fanout-multiblock");\n        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;\n        var payload = new byte[20 * 1024 * 1024 + 137];\n        new Random(12345).NextBytes(payload);\n        await File.WriteAllBytesAsync(Path.Combine(source, "multi.bin"), payload);\n        var destinations = new[]\n        {\n            Directory.CreateDirectory(Path.Combine(temp.Path, "dest-1")).FullName,\n            Directory.CreateDirectory(Path.Combine(temp.Path, "dest-2")).FullName,\n        };\n\n        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: true);\n        await using var job = CopyEngine.Start(plan);\n        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));\n        AssertHealthy(job);\n\n        foreach (var destination in destinations)\n        {\n            var copied = await File.ReadAllBytesAsync(Path.Combine(destination, "Origen", "multi.bin"));\n            CollectionAssert.AreEqual(payload, copied);\n        }\n    }\n\n'''
text = text.replace(anchor, insert + anchor, 1)

text = text.replace(
    '        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));\n\n        foreach (var destinationBase in new[] { baseOne, baseTwo })',
    '        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));\n        AssertHealthy(job);\n\n        foreach (var destinationBase in new[] { baseOne, baseTwo })',
    1)
text = text.replace(
    '        await job.Completion.WaitAsync(TimeSpan.FromSeconds(20));\n\n        Assert.AreEqual("solo este archivo"',
    '        await job.Completion.WaitAsync(TimeSpan.FromSeconds(20));\n        AssertHealthy(job);\n\n        Assert.AreEqual("solo este archivo"',
    1)

helper_anchor = '''    private sealed class TempDirectory : IDisposable'''
if helper_anchor not in text:
    raise SystemExit("expected TempDirectory anchor")
helper = '''    private static void AssertHealthy(CopyJob job)\n    {\n        var snapshots = job.Snapshot();\n        var bad = snapshots.Where(item => item.Phase != DestinationPhase.Done).ToArray();\n        if (bad.Length == 0) return;\n        Assert.Fail(string.Join(" | ", bad.Select(item => $"{item.Label}: {item.Phase}: {item.Error}")));\n    }\n\n'''
text = text.replace(helper_anchor, helper + helper_anchor, 1)
tests.write_text(text, encoding="utf-8")

winui = Path("dotnet/RepartoCopier.WinUI/MainWindow.xaml.cs")
text = winui.read_text(encoding="utf-8")
old = "!args[1].StartsWith('-', StringComparison.Ordinal)"
new = "!args[1].StartsWith(\"-\", StringComparison.Ordinal)"
if text.count(old) != 1:
    raise SystemExit("expected one command-line StartsWith anchor")
text = text.replace(old, new, 1)
old = '''        finally
        {
            if (!ReferenceEquals(_job, observed)) return;
            RefreshProgress();
            _progressTimer.Stop();
            var snapshots = observed.Snapshot();
            var failed = snapshots.Count(item => item.Phase == DestinationPhase.Failed);
            var cancelled = snapshots.Any(item => item.Phase == DestinationPhase.Cancelled);
            StatusText.Text = cancelled
                ? "Copia cancelada"
                : failed > 0
                    ? $"Finalizado con {failed} destino(s) fallido(s)"
                    : "Copia completada y verificada";
            await observed.DisposeAsync();
            _job = null;
            SetEditingEnabled(true);
            StartButton.IsEnabled = true;
            PauseButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            PauseButton.Content = "Pausar";
        }
'''
new = '''        finally
        {
            if (ReferenceEquals(_job, observed))
            {
                RefreshProgress();
                _progressTimer.Stop();
                var snapshots = observed.Snapshot();
                var failed = snapshots.Count(item => item.Phase == DestinationPhase.Failed);
                var cancelled = snapshots.Any(item => item.Phase == DestinationPhase.Cancelled);
                StatusText.Text = cancelled
                    ? "Copia cancelada"
                    : failed > 0
                        ? $"Finalizado con {failed} destino(s) fallido(s)"
                        : "Copia completada y verificada";
                await observed.DisposeAsync();
                _job = null;
                SetEditingEnabled(true);
                StartButton.IsEnabled = true;
                PauseButton.IsEnabled = false;
                CancelButton.IsEnabled = false;
                PauseButton.Content = "Pausar";
            }
        }
'''
if text.count(old) != 1:
    raise SystemExit("expected one ObserveJobCompletionAsync finally anchor")
text = text.replace(old, new, 1)
winui.write_text(text, encoding="utf-8")
