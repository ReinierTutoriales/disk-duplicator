from pathlib import Path


def replace_exact(text: str, old: str, new: str, expected: int, label: str) -> str:
    count = text.count(old)
    if count != expected:
        raise RuntimeError(f"{label}: expected {expected} matches, found {count}")
    return text.replace(old, new)

reader_path = Path("dotnet/RepartoCopier.Core/DirectIoSourceReader.cs")
reader = reader_path.read_text(encoding="utf-8")
old_reader = '''    internal static bool TryOpenOverlappedForVerification(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        out OverlappedSession? session)
    {
        session = null;
        if (!IsVerificationEligible(device, transferSize))
            return false;

        var handle = NativeMethods.CreateFileW(
            path,
            GenericRead,
            FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagNoBuffering | FileFlagSequentialScan | FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        session = new OverlappedSession(handle, RequiredAlignment(device));
        return true;
    }
'''
new_reader = '''    internal static bool TryOpenOverlapped(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        out OverlappedSession? session) =>
        TryOpenOverlappedCore(path, device, transferSize, verification: false, out session);

    internal static bool TryOpenOverlappedForVerification(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        out OverlappedSession? session) =>
        TryOpenOverlappedCore(path, device, transferSize, verification: true, out session);

    private static bool TryOpenOverlappedCore(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        bool verification,
        out OverlappedSession? session)
    {
        session = null;
        var eligible = verification
            ? IsVerificationEligible(device, transferSize)
            : IsEligible(device, transferSize);
        if (!eligible)
            return false;

        var handle = NativeMethods.CreateFileW(
            path,
            GenericRead,
            FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagNoBuffering | FileFlagSequentialScan | FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        session = new OverlappedSession(handle, RequiredAlignment(device));
        return true;
    }
'''
reader = replace_exact(reader, old_reader, new_reader, 1, "generalize overlapped direct reader")
reader_path.write_text(reader, encoding="utf-8", newline="\n")

engine_path = Path("dotnet/RepartoCopier.Core/CopyEngine.cs")
engine = engine_path.read_text(encoding="utf-8")
engine = replace_exact(
    engine,
    "        DirectIoSourceReader.Session? direct = null;\n",
    "        DirectIoSourceReader.OverlappedSession? direct = null;\n",
    1,
    "source direct session type")
engine = replace_exact(
    engine,
    "            if (!DirectIoSourceReader.TryOpen(entry.SourcePath, sourceDevice, readBufferSize, out direct))\n",
    "            if (!DirectIoSourceReader.TryOpenOverlapped(entry.SourcePath, sourceDevice, readBufferSize, out direct))\n",
    1,
    "source overlapped open")
engine = replace_exact(
    engine,
    "                            read = direct.Read(lease, readBufferSize);\n",
    "                            read = await direct.ReadAsync(lease, readBufferSize, totalRead, token).ConfigureAwait(false);\n",
    1,
    "source async direct read")
engine_path.write_text(engine, encoding="utf-8", newline="\n")

# Permanent regression gate: the main-copy source fast path must expose and use
# the same OVERLAPPED session type as verification. This prevents a future
# regression back to the synchronous Session.Read hot path.
test_path = Path("dotnet/RepartoCopier.Core.Tests/DirectIoSourceReaderTests.cs")
test = test_path.read_text(encoding="utf-8")
anchor = '''    [TestMethod]
    public void BufferedLeaseKeepsExistingArrayPoolContract()
'''
insert = '''    [TestMethod]
    public void SourceFastPathExposesOverlappedDirectIoSession()
    {
        var method = typeof(DirectIoSourceReader).GetMethod(
            "TryOpenOverlapped",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.IsNotNull(method);
        var parameters = method.GetParameters();
        Assert.AreEqual(typeof(DirectIoSourceReader.OverlappedSession).MakeByRefType(), parameters[^1].ParameterType);
    }

'''
test = replace_exact(test, anchor, insert + anchor, 1, "H-10 regression test")
test_path.write_text(test, encoding="utf-8", newline="\n")

print("H-10 overlapped source-read patch applied")
