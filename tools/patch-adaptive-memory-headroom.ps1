$ErrorActionPreference = 'Stop'
$path = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$text = [IO.File]::ReadAllText($path)

function Replace-Exact([string]$Old, [string]$New, [string]$Label) {
    if (-not $script:text.Contains($Old)) { throw "${Label}: exact pattern not found" }
    $script:text = $script:text.Replace($Old, $New)
}

Replace-Exact @'
    private const long BufferBudgetGrowthStep = 256L * 1024 * 1024;
'@ '' 'remove fixed memory growth step'

Replace-Exact @'
        private bool TryGrowLocked(int bytes, long safeCapacity)
        {
            if (_usedBytes + bytes > safeCapacity || _targetBytes >= safeCapacity)
                return false;

            var requestedTarget = Math.Max(
                _targetBytes > long.MaxValue - BufferBudgetGrowthStep
                    ? long.MaxValue
                    : _targetBytes + BufferBudgetGrowthStep,
                _usedBytes + bytes);
            var next = Math.Min(requestedTarget, safeCapacity);
            if (next <= _targetBytes)
                return false;
            _targetBytes = next;
            return true;
        }
'@ @'
        private bool TryGrowLocked(int bytes, long safeCapacity)
        {
            if (_usedBytes + bytes > safeCapacity || _targetBytes >= safeCapacity)
                return false;

            var doubled = _targetBytes >= long.MaxValue / 2
                ? long.MaxValue
                : _targetBytes * 2;
            var requestedTarget = Math.Max(doubled, _usedBytes + bytes);
            var next = Math.Min(requestedTarget, safeCapacity);
            if (next <= _targetBytes)
                return false;
            _targetBytes = next;
            return true;
        }
'@ 'make fanout memory growth multiplicative'

Replace-Exact @'
        private static long GetSystemSafeCapacity(long usedBytes)
        {
            var memory = GetMemoryStatus();
            var available = checked((long)Math.Min(memory.ullAvailPhys, (ulong)long.MaxValue));
            var total = checked((long)Math.Min(memory.ullTotalPhys, (ulong)long.MaxValue));
            var reserve = Math.Max(2L * 1024 * 1024 * 1024, total / 4);
            var additional = Math.Max(0L, available - reserve);
            var safe = additional >= long.MaxValue - usedBytes ? long.MaxValue : usedBytes + additional;
            // Even under memory pressure the engine must be able to make forward progress
            // with one source block; this is a floor, never an upper throughput ceiling.
            return Math.Max((long)BlockSize, safe);
        }
'@ @'
        private static long GetSystemSafeCapacity(long usedBytes) =>
            MemoryPressureCapacity.GetSafeTotalBytes(usedBytes, BlockSize);
'@ 'replace fixed 25-percent RAM reserve with pressure headroom'

$oldNative = @'
        private static MemoryStatusEx GetMemoryStatus()
        {
            var status = new MemoryStatusEx
            {
                dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>(),
            };
            if (!GlobalMemoryStatusEx(ref status))
                throw new IOException($"No se pudo consultar la memoria física de Windows: {Marshal.GetLastWin32Error()}.");
            return status;
        }

        private sealed class Waiter(int bytes)
        {
            public int Bytes { get; } = bytes;
            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Cancelled { get; set; }
            public bool Granted { get; set; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
'@
$newNative = @'
        private sealed class Waiter(int bytes)
        {
            public int Bytes { get; } = bytes;
            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Cancelled { get; set; }
            public bool Granted { get; set; }
        }
'@
Replace-Exact $oldNative $newNative 'remove obsolete fixed-memory native probe'

if ($text.Contains('BufferBudgetGrowthStep')) { throw 'Fixed buffer growth step remains.' }
if ($text.Contains('GlobalMemoryStatusEx')) { throw 'Legacy fixed-reserve memory probe remains.' }
if ($text.Contains('total / 4')) { throw 'Fixed 25-percent RAM reserve remains.' }

[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $path
if (git diff --cached --quiet) { throw 'Adaptive memory migration produced no changes.' }
git commit -m 'perf(core): use pressure-adaptive memory headroom'
git push origin HEAD:main
