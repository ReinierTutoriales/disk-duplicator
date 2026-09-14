from pathlib import Path


def replace_exact(text: str, old: str, new: str, expected: int, label: str) -> str:
    count = text.count(old)
    if count != expected:
        raise RuntimeError(f"{label}: expected {expected} matches, found {count}")
    return text.replace(old, new)

# 1) Collapse HashFileAsync to its only live responsibility: SkipSame BLAKE3.
engine_path = Path("dotnet/RepartoCopier.Core/CopyEngine.cs")
engine = engine_path.read_text(encoding="utf-8")
engine = replace_exact(
    engine,
    "    private const int VerificationReadBufferSize = 8 * 1024 * 1024;\n    private const int VerificationDirectIoThreshold = 4 * 1024 * 1024;\n",
    "",
    1,
    "remove dead legacy verification constants")
start = engine.index("    private static async Task<byte[]> HashFileAsync(\n")
end = engine.index("\n    private static void CommitPart(", start)
new_hash = '''    private static async Task<byte[]> HashFileAsync(\n        string path,\n        CancellationToken token,\n        ResourceGovernor? resources = null)\n    {\n        using var hasher = Hasher.New();\n        const int bufferSize = 4 * 1024 * 1024;\n        using var buffer = SourceBufferLease.RentBuffered(bufferSize);\n        await using var stream = OpenSourceStream(path);\n\n        while (true)\n        {\n            var read = await stream.ReadAsync(buffer.Memory, token).ConfigureAwait(false);\n            if (read == 0)\n                break;\n\n            if (resources is null)\n            {\n                hasher.UpdateWithJoin(buffer.Memory.Span[..read]);\n            }\n            else\n            {\n                using var lease = await resources.EnterCpuWorkAsync(token).ConfigureAwait(false);\n                hasher.UpdateWithJoin(buffer.Memory.Span[..read]);\n            }\n        }\n\n        return hasher.Finalize().AsSpan().ToArray();\n    }\n'''
engine = engine[:start] + new_hash + engine[end:]
engine_path.write_text(engine, encoding="utf-8", newline="\n")

# 2) Remove the obsolete synchronous Direct-I/O API and ReadFile P/Invoke.
reader_path = Path("dotnet/RepartoCopier.Core/DirectIoSourceReader.cs")
reader = reader_path.read_text(encoding="utf-8")
reader = replace_exact(reader, "using System.ComponentModel;\n", "", 1, "remove obsolete Win32Exception using")
start = reader.index("    internal static bool TryOpen(\n")
end = reader.index("    internal static bool TryOpenOverlapped(\n", start)
reader = reader[:start] + reader[end:]
start = reader.index("    internal sealed class Session : IDisposable\n")
end = reader.index("    internal sealed class OverlappedSession : IDisposable\n", start)
reader = reader[:start] + reader[end:]
old_readfile = '''\n        [DllImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]\n        [return: MarshalAs(UnmanagedType.Bool)]\n        internal static extern bool ReadFile(\n            SafeFileHandle file,\n            IntPtr buffer,\n            uint bytesToRead,\n            out uint bytesRead,\n            IntPtr overlapped);\n'''
reader = replace_exact(reader, old_readfile, "", 1, "remove obsolete synchronous ReadFile PInvoke")
reader_path.write_text(reader, encoding="utf-8", newline="\n")

# 3) Architecture gates: only overlapped direct I/O is allowed, and HashFileAsync
# must remain a 3-parameter SkipSame/BLAKE3 helper.
test_path = Path("dotnet/RepartoCopier.Core.Tests/DirectIoSourceReaderTests.cs")
test = test_path.read_text(encoding="utf-8")
anchor = '''    [TestMethod]\n    public void BufferedLeaseKeepsExistingArrayPoolContract()\n'''
insert = '''    [TestMethod]\n    public void DirectIoReaderHasNoSynchronousSessionOrOpenEntryPoints()\n    {\n        var nested = typeof(DirectIoSourceReader)\n            .GetNestedTypes(System.Reflection.BindingFlags.NonPublic)\n            .Select(type => type.Name)\n            .ToArray();\n        CollectionAssert.DoesNotContain(nested, "Session");\n\n        var methods = typeof(DirectIoSourceReader)\n            .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)\n            .Select(method => method.Name)\n            .ToArray();\n        CollectionAssert.DoesNotContain(methods, "TryOpen");\n        CollectionAssert.DoesNotContain(methods, "TryOpenForVerification");\n    }\n\n    [TestMethod]\n    public void SkipSameHashHelperKeepsMinimalThreeParameterShape()\n    {\n        var method = typeof(CopyEngine).GetMethod(\n            "HashFileAsync",\n            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);\n\n        Assert.IsNotNull(method);\n        var parameters = method.GetParameters();\n        Assert.AreEqual(3, parameters.Length);\n        Assert.AreEqual(typeof(string), parameters[0].ParameterType);\n        Assert.AreEqual(typeof(CancellationToken), parameters[1].ParameterType);\n        Assert.AreEqual(typeof(ResourceGovernor), parameters[2].ParameterType);\n    }\n\n'''
test = replace_exact(test, anchor, insert + anchor, 1, "insert H-11 architecture gates")
test_path.write_text(test, encoding="utf-8", newline="\n")

print("H-11 dead verification/direct-I/O surface removed")
