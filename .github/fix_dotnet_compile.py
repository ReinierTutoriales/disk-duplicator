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
engine.write_text(text, encoding="utf-8")

tests = Path("dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs")
text = tests.read_text(encoding="utf-8")
old = "Assert.ThrowsException<ArgumentException>(() =>\n            CopyPlan.Create(\"C:/Origen\", [\"C:/Origen/\"], true, true));"
new = "Assert.ThrowsExactly<ArgumentException>(() =>\n            CopyPlan.Create(\"C:/Origen\", [\"C:/Origen/\"], true, true));"
if text.count(old) != 1:
    raise SystemExit("expected one MSTest assertion anchor")
tests.write_text(text.replace(old, new, 1), encoding="utf-8")
