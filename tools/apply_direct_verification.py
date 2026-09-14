from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"Expected one match for {label}, found {count}")
    return text.replace(old, new, 1)


def replace_between(text: str, start: str, end: str, replacement: str, label: str) -> str:
    start_index = text.find(start)
    if start_index < 0:
        raise RuntimeError(f"Start marker not found for {label}")
    if text.find(start, start_index + len(start)) >= 0:
        raise RuntimeError(f"Start marker not unique for {label}")
    end_index = text.find(end, start_index)
    if end_index < 0:
        raise RuntimeError(f"End marker not found for {label}")
    return text[:start_index] + replacement.rstrip() + "\n\n" + text[end_index:]


direct_path = Path("dotnet/RepartoCopier.Core/DirectIoSourceReader.cs")
direct = direct_path.read_text(encoding="utf-8")
eligibility = '''    internal static bool IsEligible(StorageDeviceInfo device, int transferSize) =>
        IsEligibleCore(device, transferSize, solidStateOnly: true);

    internal static bool IsVerificationEligible(StorageDeviceInfo device, int transferSize) =>
        IsEligibleCore(device, transferSize, solidStateOnly: false);

    private static bool IsEligibleCore(StorageDeviceInfo device, int transferSize, bool solidStateOnly)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!OperatingSystem.IsWindows() || transferSize <= 0 || device.IsNetwork || !device.ProbeSucceeded)
            return false;
        if (StorageDeviceIdentity.ConfidenceFor(device) != DeviceIdentityConfidence.Exact)
            return false;
        if (solidStateOnly && device.MediaKind != StorageMediaKind.SolidState)
            return false;
        if (!device.HasKnownSectorAlignment)
            return false;

        var alignment = RequiredAlignment(device);
        return alignment is >= 512 and <= 64 * 1024 &&
               IsPowerOfTwo(alignment) &&
               transferSize % alignment == 0;
    }'''
direct = replace_between(
    direct,
    "    internal static bool IsEligible(StorageDeviceInfo device, int transferSize)",
    "    internal static int RequiredAlignment",
    eligibility,
    "eligibility policy",
)

open_block = '''    internal static bool TryOpen(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        out Session? session) =>
        TryOpenCore(path, device, transferSize, verification: false, out session);

    internal static bool TryOpenForVerification(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        out Session? session) =>
        TryOpenCore(path, device, transferSize, verification: true, out session);

    private static bool TryOpenCore(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        bool verification,
        out Session? session)
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
            FileFlagNoBuffering | FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        session = new Session(handle, RequiredAlignment(device));
        return true;
    }'''
direct = replace_between(
    direct,
    "    internal static bool TryOpen(",
    "    internal static bool IsFallbackable",
    open_block,
    "direct open policy",
)
direct_path.write_text(direct, encoding="utf-8", newline="\n")

engine_path = Path("dotnet/RepartoCopier.Core/CopyEngine.cs")
engine = engine_path.read_text(encoding="utf-8")
engine = replace_once(
    engine,
    "    private const int LargeBufferSize = 4 * 1024 * 1024;",
    "    private const int LargeBufferSize = 4 * 1024 * 1024;\n"
    "    private const int VerificationReadBufferSize = 8 * 1024 * 1024;\n"
    "    private const int VerificationDirectIoThreshold = 4 * 1024 * 1024;",
    "verification constants",
)

old_verify_call = '''                var actual = await HashFileAsync(
                    destination,
                    job.Token,
                    resources,
                    job.Telemetry,
                    verification: true,
                    verificationProgress: progress[slot]).ConfigureAwait(false);'''
new_verify_call = '''                var actual = await HashFileAsync(
                    destination,
                    job.Token,
                    resources,
                    job.Telemetry,
                    verification: true,
                    verificationProgress: progress[slot],
                    directDevice: copy.DestinationDevices[slot],
                    directScheduler: workers[slot].DeviceScheduler).ConfigureAwait(false);'''
engine = replace_once(engine, old_verify_call, new_verify_call, "verification call")

hash_method = '''    private static async Task<byte[]> HashFileAsync(
        string path,
        CancellationToken token,
        ResourceGovernor? resources = null,
        CopyTelemetry? telemetry = null,
        bool verification = false,
        DestinationProgress? verificationProgress = null,
        StorageDeviceInfo? directDevice = null,
        DeviceScheduler? directScheduler = null)
    {
        using var hasher = Hasher.New();
        var bufferSize = verification ? VerificationReadBufferSize : 4 * 1024 * 1024;
        var fileLength = verification ? new FileInfo(path).Length : 0L;
        SourceBufferLease? buffer = null;
        DirectIoSourceReader.Session? direct = null;
        FileStream? buffered = null;
        long totalRead = 0;

        try
        {
            var directOpened = verification &&
                fileLength >= VerificationDirectIoThreshold &&
                directDevice is not null &&
                DirectIoSourceReader.TryOpenForVerification(path, directDevice, bufferSize, out direct);

            if (directOpened)
            {
                buffer = SourceBufferLease.RentAligned(bufferSize, direct!.Alignment);
            }
            else
            {
                buffer = SourceBufferLease.RentBuffered(bufferSize);
                buffered = OpenSourceStream(path);
            }

            while (true)
            {
                var readStarted = Stopwatch.GetTimestamp();
                int read;
                try
                {
                    if (directScheduler is null)
                    {
                        read = direct is not null
                            ? direct.Read(buffer, bufferSize)
                            : await buffered!.ReadAsync(buffer.Memory, token).ConfigureAwait(false);
                    }
                    else
                    {
                        using var ioLease = await directScheduler.AcquireIoAsync(token).ConfigureAwait(false);
                        read = direct is not null
                            ? direct.Read(buffer, bufferSize)
                            : await buffered!.ReadAsync(buffer.Memory, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (direct is not null && DirectIoSourceReader.IsFallbackable(ex))
                {
                    direct.Dispose();
                    direct = null;
                    buffer.Dispose();
                    buffer = SourceBufferLease.RentBuffered(bufferSize);
                    buffered = OpenSourceStream(path);
                    buffered.Position = totalRead;

                    if (directScheduler is null)
                    {
                        read = await buffered.ReadAsync(buffer.Memory, token).ConfigureAwait(false);
                    }
                    else
                    {
                        using var ioLease = await directScheduler.AcquireIoAsync(token).ConfigureAwait(false);
                        read = await buffered.ReadAsync(buffer.Memory, token).ConfigureAwait(false);
                    }
                }

                var readElapsed = Stopwatch.GetElapsedTime(readStarted);
                if (verification)
                    telemetry?.RecordVerifyRead(read, readElapsed);
                if (read == 0)
                    break;

                totalRead += read;
                if (verification)
                    verificationProgress?.AddVerified(read);

                if (resources is null)
                {
                    var hashStarted = Stopwatch.GetTimestamp();
                    hasher.UpdateWithJoin(buffer.Memory.Span[..read]);
                    if (verification)
                        telemetry?.RecordVerifyHash(read, Stopwatch.GetElapsedTime(hashStarted));
                }
                else
                {
                    var cpuWaitStarted = Stopwatch.GetTimestamp();
                    using var lease = await resources.EnterCpuWorkAsync(token).ConfigureAwait(false);
                    if (verification)
                        telemetry?.RecordVerifyCpuWait(Stopwatch.GetElapsedTime(cpuWaitStarted));
                    var hashStarted = Stopwatch.GetTimestamp();
                    hasher.UpdateWithJoin(buffer.Memory.Span[..read]);
                    if (verification)
                        telemetry?.RecordVerifyHash(read, Stopwatch.GetElapsedTime(hashStarted));
                }
            }
            return hasher.Finalize().AsSpan().ToArray();
        }
        finally
        {
            direct?.Dispose();
            if (buffered is not null)
                await buffered.DisposeAsync().ConfigureAwait(false);
            buffer?.Dispose();
        }
    }'''
engine = replace_between(
    engine,
    "    private static async Task<byte[]> HashFileAsync(",
    "    private static void CommitPart(",
    hash_method,
    "verification hash method",
)
engine_path.write_text(engine, encoding="utf-8", newline="\n")

test_path = Path("dotnet/RepartoCopier.Core.Tests/DirectIoSourceReaderTests.cs")
tests = test_path.read_text(encoding="utf-8")
anchor = '''    [TestMethod]
    public void BufferedLeaseKeepsExistingArrayPoolContract()
'''
new_test = '''    [TestMethod]
    public void VerificationEligibilityAllowsExactLocalHddButRejectsUnsafeTopology()
    {
        var hdd = Device("SATA", StorageMediaKind.Rotational, 512, 4096);
        var network = Device("Network", StorageMediaKind.Rotational, 512, 4096) with
        {
            IsNetwork = true,
            PhysicalDeviceNumber = null,
        };
        var unknownIdentity = Device("USB", StorageMediaKind.Rotational, 512, 4096) with
        {
            PhysicalDeviceNumber = null,
        };

        Assert.IsTrue(DirectIoSourceReader.IsVerificationEligible(hdd, 8 * 1024 * 1024));
        Assert.IsFalse(DirectIoSourceReader.IsVerificationEligible(network, 8 * 1024 * 1024));
        Assert.IsFalse(DirectIoSourceReader.IsVerificationEligible(unknownIdentity, 8 * 1024 * 1024));
        Assert.IsFalse(DirectIoSourceReader.IsVerificationEligible(hdd, 8 * 1024 * 1024 - 1));
    }

    [TestMethod]
    public void BufferedLeaseKeepsExistingArrayPoolContract()
'''
tests = replace_once(tests, anchor, new_test, "verification eligibility test")
test_path.write_text(tests, encoding="utf-8", newline="\n")
