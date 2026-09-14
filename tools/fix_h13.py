from pathlib import Path


def replace_exact(text: str, old: str, new: str, expected: int, label: str) -> str:
    count = text.count(old)
    if count != expected:
        raise RuntimeError(f"{label}: expected {expected} matches, found {count}")
    return text.replace(old, new)

telemetry_path = Path("dotnet/RepartoCopier.Core/CopyTelemetry.cs")
telemetry = telemetry_path.read_text(encoding="utf-8")
telemetry = replace_exact(
    telemetry,
    "    TimeSpan VerifyHashTime,\n    TimeSpan VerifyCpuWaitTime,\n    int PeakControlBacklogMessages,\n",
    "    TimeSpan VerifyHashTime,\n    int PeakControlBacklogMessages,\n",
    1,
    "remove dead VerifyCpuWait snapshot field")
telemetry = replace_exact(
    telemetry,
    "    private long _verifyHashBytes, _verifyHashTicks, _verifyCpuWaitTicks;\n",
    "    private long _verifyHashBytes, _verifyHashTicks;\n",
    1,
    "remove dead VerifyCpuWait storage")
telemetry = replace_exact(
    telemetry,
    "    internal void RecordVerifyCpuWait(TimeSpan elapsed) => AddTicks(ref _verifyCpuWaitTicks, elapsed);\n",
    "",
    1,
    "remove dead VerifyCpuWait producer")
telemetry = replace_exact(
    telemetry,
    "            Interlocked.Read(ref _verifyHashBytes), ToTimeSpan(Interlocked.Read(ref _verifyHashTicks)),\n            ToTimeSpan(Interlocked.Read(ref _verifyCpuWaitTicks)),\n            Volatile.Read(ref _peakControlBacklogMessages),\n",
    "            Interlocked.Read(ref _verifyHashBytes), ToTimeSpan(Interlocked.Read(ref _verifyHashTicks)),\n            Volatile.Read(ref _peakControlBacklogMessages),\n",
    1,
    "remove dead VerifyCpuWait snapshot argument")
telemetry_path.write_text(telemetry, encoding="utf-8", newline="\n")

report_path = Path("dotnet/RepartoCopier.Core/DiagnosticsReport.cs")
report = report_path.read_text(encoding="utf-8")
report = replace_exact(
    report,
    "        AppendDuration(sb, \"VerifyCpuWait\", metrics.VerifyCpuWaitTime);\n",
    "",
    1,
    "remove permanently-zero VerifyCpuWait diagnostic")
report_path.write_text(report, encoding="utf-8", newline="\n")

# Update the one explicit diagnostic snapshot construction and assert the stale
# metric cannot silently reappear in the public diagnostics report.
test_path = Path("dotnet/RepartoCopier.Core.Tests/DiagnosticsReportTests.cs")
test = test_path.read_text(encoding="utf-8")
test = replace_exact(
    test,
    "            4_000_000, TimeSpan.FromMilliseconds(500),\n            TimeSpan.FromMilliseconds(80), 12,\n",
    "            4_000_000, TimeSpan.FromMilliseconds(500),\n            12,\n",
    1,
    "update diagnostic snapshot shape")
test = replace_exact(
    test,
    "        StringAssert.Contains(report, \"VerifyHashRate: 8000000 B/s\");\n",
    "        StringAssert.Contains(report, \"VerifyHashRate: 8000000 B/s\");\n        Assert.IsFalse(report.Contains(\"VerifyCpuWait\", StringComparison.Ordinal));\n",
    1,
    "gate dead VerifyCpuWait removal")
test_path.write_text(test, encoding="utf-8", newline="\n")

print("H-13 dead verify CPU-wait telemetry removed")
