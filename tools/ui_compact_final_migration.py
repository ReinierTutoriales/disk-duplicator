from pathlib import Path

root = Path(__file__).resolve().parents[1]
xaml_path = root / 'dotnet/RepartoCopier.WinUI/MainWindow.xaml'
code_path = root / 'dotnet/RepartoCopier.WinUI/MainWindow.xaml.cs'
compact_test_path = root / 'dotnet/RepartoCopier.Core.Tests/WinUiCompactProgressContractTests.cs'
verify_test_path = root / 'dotnet/RepartoCopier.Core.Tests/WinUiVerifyContractTests.cs'
direct_writer_path = root / 'dotnet/RepartoCopier.Core/DirectIoDestinationWriter.cs'
coordinator_path = root / 'dotnet/RepartoCopier.Core/DestinationWriteCoordinator.cs'
scheduler_path = root / 'dotnet/RepartoCopier.Core/DeviceScheduler.cs'
copy_engine_path = root / 'dotnet/RepartoCopier.Core/CopyEngine.cs'
direct_test_path = root / 'dotnet/RepartoCopier.Core.Tests/DirectIoDestinationWriterTests.cs'
recovery_test_path = root / 'dotnet/RepartoCopier.Core.Tests/IoRecoveryArchitectureTests.cs'
extreme_test_path = root / 'dotnet/RepartoCopier.Core.Tests/ExtremeStyleIoArchitectureTests.cs'

# UI: maximize useful density without abandoning native WinUI controls.
xaml = xaml_path.read_text(encoding='utf-8')
repls = {
    '<RowDefinition Height="26" />': '<RowDefinition Height="22" />',
    '<Grid Grid.Row="1" Margin="8,0,8,6" MaxWidth="760" HorizontalAlignment="Stretch">': '<Grid Grid.Row="1" Margin="6,0,6,4" MaxWidth="740" HorizontalAlignment="Stretch">',
    '<Grid x:Name="PreparationPanel" Visibility="Visible" RowSpacing="8" MaxWidth="744" HorizontalAlignment="Stretch">': '<Grid x:Name="PreparationPanel" Visibility="Visible" RowSpacing="6" MaxWidth="728" HorizontalAlignment="Stretch">',
    '<Border Grid.Row="1" Style="{StaticResource SurfaceCardStyle}" Padding="10">': '<Border Grid.Row="1" Style="{StaticResource SurfaceCardStyle}" Padding="8">',
    '<Grid RowSpacing="8">': '<Grid RowSpacing="6">',
    '<ListView x:Name="DestinationList" Grid.Row="3" SelectionMode="None" Height="50" MaxHeight="50"': '<ListView x:Name="DestinationList" Grid.Row="3" SelectionMode="None" Height="40" MaxHeight="40"',
    '<Setter Property="Margin" Value="0,0,6,0"/>': '<Setter Property="Margin" Value="0,0,3,0"/>',
    '<Setter Property="MinHeight" Value="42"/>': '<Setter Property="MinHeight" Value="34"/>',
    '<Border Width="158" Height="42" Padding="8,0" CornerRadius="6"': '<Border Width="86" Height="34" Padding="5,0,3,0" CornerRadius="5"',
    '<Grid ColumnSpacing="7">': '<Grid ColumnSpacing="4">',
    '<FontIcon Glyph="&#xEDA2;" FontSize="13" Opacity="0.68" VerticalAlignment="Center"/>': '<FontIcon Glyph="&#xEDA2;" FontSize="12" Opacity="0.68" VerticalAlignment="Center"/>',
    '<TextBlock Grid.Column="1" Text="{Binding Path}" FontSize="11" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>': '<TextBlock Grid.Column="1" Text="{Binding Path}" FontSize="11" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" MinWidth="22"/>',
    '<Button Grid.Column="2" Width="24" Height="24" Padding="0" CornerRadius="5" Tag="{Binding Path}" Click="RemoveDestination_Click" ToolTipService.ToolTip="Quitar">': '<Button Grid.Column="2" Width="20" Height="20" Padding="0" CornerRadius="4" Tag="{Binding Path}" Click="RemoveDestination_Click" ToolTipService.ToolTip="Quitar">',
    '<FontIcon Glyph="&#xE711;" FontSize="10"/>': '<FontIcon Glyph="&#xE711;" FontSize="9"/>',
    '<StackPanel Orientation="Horizontal" Spacing="16" Padding="0,7,0,2">\n                                <CheckBox x:Name="VerifyCheck" Content="Verificar después de copiar" IsChecked="True"/>\n                                <CheckBox x:Name="SkipSameCheck" Content="Omitir iguales"/>\n                                <CheckBox x:Name="KeepGoingCheck" Content="Continuar ante error"/>\n                            </StackPanel>': '<StackPanel Orientation="Horizontal" Spacing="12" Padding="0,5,0,0">\n                                <CheckBox x:Name="SkipSameCheck" Content="Omitir iguales"/>\n                                <CheckBox x:Name="KeepGoingCheck" Content="Continuar ante error"/>\n                            </StackPanel>',
    '<Grid x:Name="RunningPanel" Visibility="Collapsed" RowSpacing="8" MaxWidth="744" HorizontalAlignment="Stretch" VerticalAlignment="Top" Margin="0,10,0,0">': '<Grid x:Name="RunningPanel" Visibility="Collapsed" RowSpacing="6" MaxWidth="728" HorizontalAlignment="Stretch" VerticalAlignment="Top" Margin="0,6,0,0">',
    '<Border Style="{StaticResource SurfaceCardStyle}" Padding="10,8">': '<Border Style="{StaticResource SurfaceCardStyle}" Padding="8,6">',
    '<Border Grid.Row="2" Padding="16,0"': '<Border Grid.Row="2" Padding="10,0"',
}
for old, new in repls.items():
    if old not in xaml:
        raise SystemExit(f'Missing XAML pattern: {old[:100]}')
    xaml = xaml.replace(old, new, 1)
xaml_path.write_text(xaml, encoding='utf-8')

code = code_path.read_text(encoding='utf-8')
code_repls = {
    'AppWindow.Resize(new SizeInt32(760, 400));': 'AppWindow.Resize(new SizeInt32(740, 340));',
    'Verify: VerifyCheck.IsChecked == true,': 'Verify: true,',
    '            OverallDetailText.Text = options.Verify\n                ? "Preparando copia con verificación rápida..."\n                : "Preparando copia sin verificación posterior...";': '            OverallDetailText.Text = "Preparando copia con verificación rápida...";',
    '            VerifyCheck.IsChecked = profile.VerifyAfterCopy;\n': '',
    '                VerifyCheck.IsChecked == true);': '                true);',
    '        VerifyCheck.IsEnabled = enabled;\n': '',
}
for old, new in code_repls.items():
    if old not in code:
        raise SystemExit(f'Missing code pattern: {old[:100]}')
    code = code.replace(old, new, 1)
code_path.write_text(code, encoding='utf-8')

compact_test = compact_test_path.read_text(encoding='utf-8')
compact_test = compact_test.replace('Assert.IsTrue(code.Contains("SizeInt32(760, 400)", StringComparison.Ordinal));', 'Assert.IsTrue(code.Contains("SizeInt32(740, 340)", StringComparison.Ordinal));')
compact_test = compact_test.replace('Assert.IsTrue(xaml.Contains("VerticalAlignment=\\"Top\\" Margin=\\"0,10,0,0\\"", StringComparison.Ordinal));', 'Assert.IsTrue(xaml.Contains("VerticalAlignment=\\"Top\\" Margin=\\"0,6,0,0\\"", StringComparison.Ordinal));')
insert = '''\n    [TestMethod]\n    public void DestinationChipsAreDenseAndVerificationIsNotAUserToggle()\n    {\n        var root = FindRepositoryRoot();\n        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));\n        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));\n        Assert.IsTrue(xaml.Contains("Width=\\"86\\" Height=\\"34\\" Padding=\\"5,0,3,0\\"", StringComparison.Ordinal));\n        Assert.IsTrue(xaml.Contains("Width=\\"20\\" Height=\\"20\\" Padding=\\"0\\"", StringComparison.Ordinal));\n        Assert.IsTrue(xaml.Contains("Margin\\" Value=\\"0,0,3,0\\"", StringComparison.Ordinal));\n        Assert.IsFalse(xaml.Contains("VerifyCheck", StringComparison.Ordinal));\n        Assert.IsFalse(code.Contains("VerifyCheck", StringComparison.Ordinal));\n        Assert.IsTrue(code.Contains("Verify: true", StringComparison.Ordinal));\n    }\n'''
marker = '\n    private static string FindRepositoryRoot()\n'
if marker not in compact_test:
    raise SystemExit('Compact test insertion marker missing')
compact_test = compact_test.replace(marker, insert + marker, 1)
compact_test_path.write_text(compact_test, encoding='utf-8')

verify_test_path.write_text('''using Microsoft.VisualStudio.TestTools.UnitTesting;\n\nnamespace RepartoCopier.Core.Tests;\n\n[TestClass]\npublic sealed class WinUiVerifyContractTests\n{\n    [TestMethod]\n    public void VerificationIsAlwaysEnabledAndHiddenFromThePrimaryUi()\n    {\n        var root = FindRepositoryRoot();\n        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));\n        var codeBehind = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));\n        Assert.IsFalse(xaml.Contains("VerifyCheck", StringComparison.Ordinal));\n        Assert.IsFalse(codeBehind.Contains("VerifyCheck", StringComparison.Ordinal));\n        Assert.IsTrue(codeBehind.Contains("Verify: true", StringComparison.Ordinal));\n    }\n\n    [TestMethod]\n    public void LegacyProfileVerificationChoiceCannotDisableMandatoryVerification()\n    {\n        var root = FindRepositoryRoot();\n        var profile = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "CopyProfile.cs"));\n        var codeBehind = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));\n        Assert.IsTrue(profile.Contains("bool VerifyAfterCopy = true", StringComparison.Ordinal));\n        Assert.IsFalse(codeBehind.Contains("profile.VerifyAfterCopy", StringComparison.Ordinal));\n        Assert.IsTrue(codeBehind.Contains("Verify: true", StringComparison.Ordinal));\n    }\n\n    private static string FindRepositoryRoot()\n    {\n        var current = new DirectoryInfo(AppContext.BaseDirectory);\n        while (current is not null)\n        {\n            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln"))) return current.FullName;\n            current = current.Parent;\n        }\n        throw new AssertFailedException("No se encontró la raíz del repositorio.");\n    }\n}\n''', encoding='utf-8')

# Extreme-style destination writer: synchronous WriteFile, sequential scan, optional NO_BUFFERING, no OVERLAPPED.
direct_writer_path.write_text(r'''using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Stable sequential destination writer modeled after ExtremeCopy's active path:
/// one synchronous physical write at a time per destination, SEQUENTIAL_SCAN and
/// NO_BUFFERING when safe. The shared FAN-OUT payload is never copied per destination.
/// </summary>
internal static class DirectIoDestinationWriter
{
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagSequentialScan = 0x08000000;

    internal static bool IsEligible(StorageDeviceInfo device, long fileSize)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!OperatingSystem.IsWindows() || fileSize <= 0 || device.IsNetwork || !device.ProbeSucceeded)
            return false;
        if (!device.HasKnownSectorAlignment)
            return false;

        var alignment = DirectIoSourceReader.RequiredAlignment(device);
        if (alignment < 512 || !IsPowerOfTwo(alignment))
            return false;

        // ExtremeCopy enables NO_BUFFERING for local files >= 64 KiB, or for
        // smaller files that already end on a physical-sector boundary.
        return fileSize >= 64L * 1024 || fileSize % alignment == 0;
    }

    internal static bool TryOpen(string path, StorageDeviceInfo device, long fileSize, out Session? session)
    {
        session = null;
        if (!IsEligible(device, fileSize))
            return false;

        var handle = NativeMethods.CreateFileW(
            path,
            GenericWrite,
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

        session = new Session(handle, DirectIoSourceReader.RequiredAlignment(device));
        return true;
    }

    internal static bool IsFallbackable(Exception error) =>
        TransientIoErrorClassifier.IsDirectFallbackable(error);

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    internal sealed class Session : IDisposable
    {
        private SafeFileHandle? _handle;

        internal Session(SafeFileHandle handle, int alignment)
        {
            ArgumentNullException.ThrowIfNull(handle);
            if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(alignment));
            _handle = handle;
            Alignment = alignment;
        }

        internal int Alignment { get; }

        internal async Task<int> WriteAsync(
            ReadOnlyMemory<byte> data,
            long fileOffset,
            long logicalFileLength,
            bool payloadIsAligned,
            DeviceScheduler scheduler,
            CancellationToken token)
        {
            if (data.IsEmpty)
                return 0;
            if (fileOffset < 0 || fileOffset % Alignment != 0)
                throw new DirectIoWriteException(87, "El offset no está alineado al sector físico.");
            if (!payloadIsAligned)
                throw new DirectIoWriteException(87, "El payload FAN-OUT no está alineado para Direct I/O.");
            if (logicalFileLength < 0 || fileOffset + data.Length > logicalFileLength)
                throw new ArgumentOutOfRangeException(nameof(logicalFileLength));

            var handle = _handle ?? throw new ObjectDisposedException(nameof(Session));
            var alignedLength = data.Length - data.Length % Alignment;
            var operations = 0;
            try
            {
                if (alignedLength > 0)
                {
                    using var io = await scheduler.AcquireIoAsync(alignedLength, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    WriteSynchronous(handle, data[..alignedLength], fileOffset);
                    operations++;
                }

                var tailLength = data.Length - alignedLength;
                if (tailLength > 0)
                {
                    if (fileOffset + data.Length != logicalFileLength)
                        throw new DirectIoWriteException(87, "Solo el tail final puede requerir padding de sector.");

                    using var tail = SourceBufferLease.RentAligned(Alignment, Alignment);
                    tail.Memory.Span.Clear();
                    data.Span[alignedLength..].CopyTo(tail.Memory.Span);
                    using var io = await scheduler.AcquireIoAsync(Alignment, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    WriteSynchronous(handle, tail.Memory, checked(fileOffset + alignedLength));
                    operations++;
                }

                return operations;
            }
            catch (IOException ex) when (ex is not DirectIoWriteException)
            {
                throw new DirectIoWriteException(TransientIoErrorClassifier.GetNativeCodeOrZero(ex), ex.Message);
            }
        }

        private static void WriteSynchronous(SafeFileHandle handle, ReadOnlyMemory<byte> data, long offset)
        {
            if (!MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment) || segment.Array is null)
                throw new DirectIoWriteException(87, "El buffer de escritura debe estar respaldado por el pool FAN-OUT fijado.");

            if (!NativeMethods.SetFilePointerEx(handle, offset, out _, 0))
            {
                var code = Marshal.GetLastWin32Error();
                throw new DirectIoWriteException(code, "No se pudo posicionar el handle síncrono del destino.");
            }

            var pointer = Marshal.UnsafeAddrOfPinnedArrayElement(segment.Array, segment.Offset);
            if (!NativeMethods.WriteFile(handle, pointer, checked((uint)data.Length), out var written, IntPtr.Zero))
            {
                var code = Marshal.GetLastWin32Error();
                throw new DirectIoWriteException(code, "WriteFile síncrono falló.");
            }
            if (written != data.Length)
                throw new DirectIoWriteException(1117, $"WriteFile escribió {written} de {data.Length} bytes.");
        }

        internal void FinalizeLength(long exactLength)
        {
            var handle = _handle ?? throw new ObjectDisposedException(nameof(Session));
            try { RandomAccess.SetLength(handle, exactLength); }
            catch (IOException ex)
            {
                throw new DirectIoWriteException(TransientIoErrorClassifier.GetNativeCodeOrZero(ex), ex.Message);
            }
        }

        internal void FlushToDisk()
        {
            var handle = _handle ?? throw new ObjectDisposedException(nameof(Session));
            try { RandomAccess.FlushToDisk(handle); }
            catch (IOException ex)
            {
                throw new DirectIoWriteException(TransientIoErrorClassifier.GetNativeCodeOrZero(ex), ex.Message);
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
    }

    internal sealed class DirectIoWriteException : IOException
    {
        internal DirectIoWriteException(int nativeErrorCode, string message)
            : base($"Direct I/O de escritura falló ({nativeErrorCode}): {message}") => NativeErrorCode = nativeErrorCode;
        internal int NativeErrorCode { get; }
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFilePointerEx(SafeFileHandle hFile, long distanceToMove, out long newFilePointer, uint moveMethod);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteFile(SafeFileHandle hFile, IntPtr buffer, uint numberOfBytesToWrite, out uint numberOfBytesWritten, IntPtr overlapped);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);
    }
}
''', encoding='utf-8')

coordinator_path.write_text(r'''using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// One stable synchronous write per physical destination. Different physical
/// destinations still run in parallel through their independent writer loops.
/// </summary>
internal static class DestinationWriteCoordinator
{
    internal static async Task<int> WriteAsync(
        SafeFileHandle handle,
        ReadOnlyMemory<byte> data,
        long offset,
        DeviceScheduler scheduler,
        CancellationToken token,
        int alignment = 1)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(scheduler);
        if (data.IsEmpty) return 0;
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (alignment <= 0 || offset % alignment != 0 || data.Length % alignment != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));

        using var lease = await scheduler.AcquireIoAsync(data.Length, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        RandomAccess.Write(handle, data.Span, offset);
        return 1;
    }
}
''', encoding='utf-8')

# Remove fixed retry cutoff from the QD1 scheduler. Retry policy belongs to the I/O loop.
scheduler = scheduler_path.read_text(encoding='utf-8')
scheduler = scheduler.replace('    private int _transientFailuresSinceSuccess;\n', '')
start = scheduler.find('    internal bool RecordTransientFailure()\n')
if start < 0:
    raise SystemExit('RecordTransientFailure method missing')
end = scheduler.find('    public void ReserveBacklog', start)
if end < 0:
    raise SystemExit('RecordTransientFailure method end missing')
scheduler = scheduler[:start] + scheduler[end:]
scheduler = scheduler.replace('            _transientFailuresSinceSuccess = 0;\n', '')
scheduler_path.write_text(scheduler, encoding='utf-8')

engine = copy_engine_path.read_text(encoding='utf-8')
if 'var options = FileOptions.Asynchronous | FileOptions.SequentialScan;' not in engine:
    raise SystemExit('Buffered destination FileOptions pattern missing')
engine = engine.replace('var options = FileOptions.Asynchronous | FileOptions.SequentialScan;', 'var options = FileOptions.SequentialScan;', 1)

old_direct_cutoff = '''                        if (!worker.DeviceScheduler.RecordTransientFailure())\n                        {\n                            ReleaseBranchBlock(worker, block);\n                            return PendingWriteResult.Failed(ex);\n                        }'''
if old_direct_cutoff not in engine:
    raise SystemExit('Direct fixed retry cutoff missing')
engine = engine.replace(old_direct_cutoff, '                        await DelayTransientRetryAsync(directRetryCount, job.Token).ConfigureAwait(false);', 1)

old_buffered_cutoff = '''                if (!worker.DeviceScheduler.RecordTransientFailure())\n                    break;'''
if old_buffered_cutoff not in engine:
    raise SystemExit('Buffered fixed retry cutoff missing')
engine = engine.replace(old_buffered_cutoff, '                await DelayTransientRetryAsync(bufferedRetryCount, job.Token).ConfigureAwait(false);', 1)

verify_start = engine.find('    private static async Task<int> ReadVerifyTargetAsync(\n')
verify_end = engine.find('    private static int AlignUp(', verify_start)
if verify_start < 0 or verify_end < 0:
    raise SystemExit('Verify read method boundaries missing')
verify_method = r'''    private static async Task<int> ReadVerifyTargetAsync(
        CoordinatedVerifyTarget target,
        int expectedBytes,
        long offset,
        CopyJob job)
    {
        var retryCount = 0;
        while (true)
        {
            job.Token.ThrowIfCancellationRequested();
            var requestBytes = target.Direct is null
                ? expectedBytes
                : AlignUp(expectedBytes, target.Direct.Alignment);

            IDisposable? io = null;
            if (target.Scheduler is not null)
                io = await target.Scheduler.AcquireIoAsync(requestBytes, job.Token).ConfigureAwait(false);
            using (io)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    int read;
                    if (target.Direct is not null)
                    {
                        try
                        {
                            read = await target.Direct.ReadAsync(target.Buffer!, requestBytes, offset, job.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                        {
                            target.SwitchToBuffered();
                            continue;
                        }
                    }
                    else
                    {
                        read = await RandomAccess.ReadAsync(
                            target.BufferedHandle!,
                            target.Buffer!.Memory[..expectedBytes],
                            offset,
                            job.Token).ConfigureAwait(false);
                    }

                    job.Telemetry.RecordVerifyRead(expectedBytes, Stopwatch.GetElapsedTime(started));
                    if (retryCount > 0)
                    {
                        job.Telemetry.RecordIoRecovery(
                            "verify-read", target.Path, target.Direct is null ? "buffered" : "direct", 0,
                            target.Scheduler?.CurrentQueueDepth ?? 1, retryCount, offset, recovered: true);
                    }
                    return read;
                }
                catch (Exception ex) when (TransientIoErrorClassifier.IsTransient(ex))
                {
                    retryCount++;
                    target.Progress?.AddRetry();
                    job.Telemetry.RecordIoRecovery(
                        "verify-read",
                        target.Path,
                        target.Direct is null ? "buffered" : "direct",
                        TransientIoErrorClassifier.GetNativeCodeOrZero(ex),
                        target.Scheduler?.CurrentQueueDepth ?? 1,
                        retryCount,
                        offset,
                        recovered: false);
                }
            }

            await DelayTransientRetryAsync(retryCount, job.Token).ConfigureAwait(false);
        }
    }

    private static Task DelayTransientRetryAsync(int retryCount, CancellationToken token)
    {
        // No arbitrary retry-count cutoff: recoverable USB/storage faults can settle.
        // A short capped delay prevents a disconnected device from becoming a hot spin;
        // the user can always cancel the job.
        var milliseconds = Math.Min(250, 10 * Math.Min(Math.Max(1, retryCount), 25));
        return Task.Delay(milliseconds, token);
    }

'''
engine = engine[:verify_start] + verify_method + engine[verify_end:]
copy_engine_path.write_text(engine, encoding='utf-8')

# Update tests to the new production contract.
direct_test = direct_test_path.read_text(encoding='utf-8')
direct_test = direct_test.replace('public void LocalHddAndSsdAreEligibleWithoutFixedFileSizeFloor()', 'public void LocalHddAndSsdFollowExtremeStyleNoBufferingEligibility()')
direct_test = direct_test.replace('        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(ssd, 1));', '        Assert.IsFalse(DirectIoDestinationWriter.IsEligible(ssd, 1));')
direct_test = direct_test.replace('                FileOptions.Asynchronous);', '                FileOptions.SequentialScan);')
direct_test_path.write_text(direct_test, encoding='utf-8')

recovery_test = recovery_test_path.read_text(encoding='utf-8')
old_test_start = recovery_test.find('    [TestMethod]\n    public void TransientFailuresNeverIncreaseOrDecreaseFixedQueueDepthOne()\n')
old_test_end = recovery_test.find('    [TestMethod]\n    public void DiagnosticsExposeRecoveryContext()', old_test_start)
if old_test_start < 0 or old_test_end < 0:
    raise SystemExit('Stale retry test boundaries missing')
new_test = '''    [TestMethod]\n    public void FixedSchedulerDoesNotOwnAnArbitraryRetryCutoff()\n    {\n        var root = FindRepositoryRoot();\n        var scheduler = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "DeviceScheduler.cs"));\n        Assert.IsFalse(scheduler.Contains("RecordTransientFailure", StringComparison.Ordinal));\n        Assert.IsFalse(scheduler.Contains("_transientFailuresSinceSuccess", StringComparison.Ordinal));\n    }\n\n'''
recovery_test = recovery_test[:old_test_start] + new_test + recovery_test[old_test_end:]
# This test file did not previously need repository lookup; add it now.
last_brace = recovery_test.rfind('}')
helper = '''\n    private static string FindRepositoryRoot()\n    {\n        var current = new DirectoryInfo(AppContext.BaseDirectory);\n        while (current is not null)\n        {\n            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln"))) return current.FullName;\n            current = current.Parent;\n        }\n        throw new AssertFailedException("No se encontró la raíz del repositorio.");\n    }\n'''
recovery_test = recovery_test[:last_brace] + helper + recovery_test[last_brace:]
recovery_test_path.write_text(recovery_test, encoding='utf-8')

extreme_test = extreme_test_path.read_text(encoding='utf-8')
extra = '''\n    [TestMethod]\n    public void DestinationPathUsesSynchronousSequentialWritesWithoutOverlapped()\n    {\n        var root = FindRepositoryRoot();\n        var direct = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "DirectIoDestinationWriter.cs"));\n        var coordinator = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "DestinationWriteCoordinator.cs"));\n        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));\n        Assert.IsTrue(direct.Contains("FileFlagNoBuffering | FileFlagSequentialScan", StringComparison.Ordinal));\n        Assert.IsTrue(direct.Contains("WriteFile(handle", StringComparison.Ordinal));\n        Assert.IsFalse(direct.Contains("FileFlagOverlapped", StringComparison.Ordinal));\n        Assert.IsFalse(coordinator.Contains("RandomAccess.WriteAsync", StringComparison.Ordinal));\n        Assert.IsTrue(coordinator.Contains("RandomAccess.Write(handle", StringComparison.Ordinal));\n        Assert.IsFalse(engine.Contains("FileOptions.Asynchronous | FileOptions.SequentialScan", StringComparison.Ordinal));\n    }\n\n    [TestMethod]\n    public void RecoverableIoHasNoFixedThreeFailureCutoffAndVerifyRetryIsIterative()\n    {\n        var root = FindRepositoryRoot();\n        var scheduler = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "DeviceScheduler.cs"));\n        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));\n        Assert.IsFalse(scheduler.Contains("RecordTransientFailure", StringComparison.Ordinal));\n        Assert.IsFalse(engine.Contains("RecordTransientFailure", StringComparison.Ordinal));\n        Assert.IsTrue(engine.Contains("DelayTransientRetryAsync", StringComparison.Ordinal));\n        Assert.IsFalse(engine.Contains("return await ReadVerifyTargetAsync", StringComparison.Ordinal));\n    }\n'''
marker = '\n    private static StorageDeviceInfo Device('
if marker not in extreme_test:
    raise SystemExit('Extreme test insertion marker missing')
extreme_test = extreme_test.replace(marker, extra + marker, 1)
extreme_test_path.write_text(extreme_test, encoding='utf-8')

print('Compact WinUI + Extreme-style synchronous I/O migration applied')
