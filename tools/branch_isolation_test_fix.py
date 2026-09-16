from pathlib import Path

root = Path(__file__).resolve().parents[1]
path = root / 'dotnet/RepartoCopier.Core.Tests/RetryAndReplayArchitectureTests.cs'
text = path.read_text(encoding='utf-8')

start = text.index('    [TestMethod]\n    public void ReplayGateRequiresSustainedHighBacklogAndHystereticLowExit()')
end = text.index('    private static StorageDeviceInfo Device(', start)
replacement = '''    [TestMethod]\n    public void BranchIsolationReplacesTimedReplayGateWithQueueWindowFeedback()\n    {\n        var target = BranchIsolationPolicy.SharedRetentionTargetBytes(4 * 1024 * 1024, 8, 256L * 1024 * 1024);\n        Assert.AreEqual(32L * 1024 * 1024, target);\n        Assert.IsFalse(BranchIsolationPolicy.ShouldDetach(target, 4 * 1024 * 1024, 8, 256L * 1024 * 1024));\n        Assert.IsTrue(BranchIsolationPolicy.ShouldDetach(target + 1, 4 * 1024 * 1024, 8, 256L * 1024 * 1024));\n    }\n\n'''
text = text[:start] + replacement + text[end:]
text = text.replace('using System.Diagnostics;\n', '')
path.write_text(text, encoding='utf-8')
print('retired replay gate tests replaced')
