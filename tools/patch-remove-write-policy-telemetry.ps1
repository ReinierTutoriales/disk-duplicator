$ErrorActionPreference = 'Stop'
$path = 'dotnet/RepartoCopier.Core/CopyTelemetry.cs'
$text = [IO.File]::ReadAllText($path)

function Replace-Exact([string]$Old, [string]$New, [string]$Label) {
    if (-not $script:text.Contains($Old)) { throw "${Label}: exact pattern not found" }
    $script:text = $script:text.Replace($Old, $New)
}

$policyRecord = @'
public sealed record WritePolicyDiagnosticsSnapshot(
    int Files,
    long WrittenBytes,
    TimeSpan WriteTime,
    int Commits,
    TimeSpan CommitTime,
    int RecoveryEvents,
    TimeSpan RecoveryTime)
{
    public double WriteBytesPerSecond =>
        WrittenBytes <= 0 || WriteTime <= TimeSpan.Zero ? 0 : WrittenBytes / WriteTime.TotalSeconds;
}

'@
Replace-Exact $policyRecord '' 'remove obsolete policy snapshot type'

Replace-Exact @'
    public PipelineGovernorSnapshot? PipelineGovernor { get; init; }
    public WritePolicyDiagnosticsSnapshot WriteThroughPolicy { get; init; } = EmptyWritePolicy;
    public WritePolicyDiagnosticsSnapshot BufferedPolicy { get; init; } = EmptyWritePolicy;
    public long DirectSourceReadBytes { get; init; }
'@ @'
    public PipelineGovernorSnapshot? PipelineGovernor { get; init; }
    public long DirectSourceReadBytes { get; init; }
'@ 'remove obsolete policy properties'

$emptyPolicy = @'
    private static WritePolicyDiagnosticsSnapshot EmptyWritePolicy =>
        new(0, 0, TimeSpan.Zero, 0, TimeSpan.Zero, 0, TimeSpan.Zero);

'@
Replace-Exact $emptyPolicy '' 'remove empty policy helper'

Replace-Exact @'
    private int _writeThroughFiles, _bufferedFiles;
    private long _writeThroughBytes, _writeThroughWriteTicks;
    private long _bufferedBytes, _bufferedWriteTicks;
    private int _writeThroughCommits, _bufferedCommits;
    private long _writeThroughCommitTicks, _bufferedCommitTicks;
    private int _writeThroughRecoveryEvents, _bufferedRecoveryEvents;
    private long _writeThroughRecoveryTicks, _bufferedRecoveryTicks;
'@ '' 'remove obsolete policy counters'

$recordFilePolicy = @'
    internal void RecordFilePolicy(bool writeThrough)
    {
        if (writeThrough)
            Interlocked.Increment(ref _writeThroughFiles);
        else
            Interlocked.Increment(ref _bufferedFiles);
    }

'@
Replace-Exact $recordFilePolicy '' 'remove file policy recorder'

$oldRecordWrite = @'
    internal void RecordWrite(int bytes, TimeSpan elapsed, bool writeThrough)
    {
        AddBytes(ref _writtenBytes, bytes);
        AddTicks(ref _writeTicks, elapsed);
        if (writeThrough)
        {
            AddBytes(ref _writeThroughBytes, bytes);
            AddTicks(ref _writeThroughWriteTicks, elapsed);
        }
        else
        {
            AddBytes(ref _bufferedBytes, bytes);
            AddTicks(ref _bufferedWriteTicks, elapsed);
        }

        if (bytes <= 0) return;
'@
$newRecordWrite = @'
    internal void RecordWrite(int bytes, TimeSpan elapsed)
    {
        AddBytes(ref _writtenBytes, bytes);
        AddTicks(ref _writeTicks, elapsed);

        if (bytes <= 0) return;
'@
Replace-Exact $oldRecordWrite $newRecordWrite 'unify write telemetry'

$oldCommit = @'
    internal void RecordCommit(TimeSpan elapsed, bool writeThrough)
    {
        Interlocked.Increment(ref _commits);
        AddTicks(ref _commitTicks, elapsed);
        if (writeThrough)
        {
            Interlocked.Increment(ref _writeThroughCommits);
            AddTicks(ref _writeThroughCommitTicks, elapsed);
        }
        else
        {
            Interlocked.Increment(ref _bufferedCommits);
            AddTicks(ref _bufferedCommitTicks, elapsed);
        }
    }

    internal void RecordRecovery(TimeSpan elapsed, bool writeThrough)
    {
        Interlocked.Increment(ref _recoveryEvents);
        AddTicks(ref _recoveryTicks, elapsed);
        if (writeThrough)
        {
            Interlocked.Increment(ref _writeThroughRecoveryEvents);
            AddTicks(ref _writeThroughRecoveryTicks, elapsed);
        }
        else
        {
            Interlocked.Increment(ref _bufferedRecoveryEvents);
            AddTicks(ref _bufferedRecoveryTicks, elapsed);
        }
    }
'@
$newCommit = @'
    internal void RecordCommit(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _commits);
        AddTicks(ref _commitTicks, elapsed);
    }

    internal void RecordRecovery(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _recoveryEvents);
        AddTicks(ref _recoveryTicks, elapsed);
    }
'@
Replace-Exact $oldCommit $newCommit 'unify commit and recovery telemetry'

$policyBuild = @'
        var writeThrough = new WritePolicyDiagnosticsSnapshot(
            Volatile.Read(ref _writeThroughFiles),
            Interlocked.Read(ref _writeThroughBytes),
            ToTimeSpan(Interlocked.Read(ref _writeThroughWriteTicks)),
            Volatile.Read(ref _writeThroughCommits),
            ToTimeSpan(Interlocked.Read(ref _writeThroughCommitTicks)),
            Volatile.Read(ref _writeThroughRecoveryEvents),
            ToTimeSpan(Interlocked.Read(ref _writeThroughRecoveryTicks)));
        var buffered = new WritePolicyDiagnosticsSnapshot(
            Volatile.Read(ref _bufferedFiles),
            Interlocked.Read(ref _bufferedBytes),
            ToTimeSpan(Interlocked.Read(ref _bufferedWriteTicks)),
            Volatile.Read(ref _bufferedCommits),
            ToTimeSpan(Interlocked.Read(ref _bufferedCommitTicks)),
            Volatile.Read(ref _bufferedRecoveryEvents),
            ToTimeSpan(Interlocked.Read(ref _bufferedRecoveryTicks)));

'@
Replace-Exact $policyBuild '' 'remove obsolete policy snapshot construction'

Replace-Exact @'
            DeviceSchedulers = devices,
            PipelineGovernor = pipeline,
            WriteThroughPolicy = writeThrough,
            BufferedPolicy = buffered,
            DirectSourceReadBytes = Interlocked.Read(ref _directSourceReadBytes),
'@ @'
            DeviceSchedulers = devices,
            PipelineGovernor = pipeline,
            DirectSourceReadBytes = Interlocked.Read(ref _directSourceReadBytes),
'@ 'remove obsolete policy snapshot assignment'

$forbidden = @('WriteThroughPolicy','BufferedPolicy','WritePolicyDiagnosticsSnapshot','RecordFilePolicy','_writeThrough','_bufferedFiles','_bufferedBytes','_bufferedWriteTicks','_bufferedCommits','_bufferedCommitTicks','_bufferedRecovery')
foreach ($symbol in $forbidden) {
    if ($text.Contains($symbol)) { throw "Obsolete write-policy telemetry remains: $symbol" }
}

[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $path
if (git diff --cached --quiet) { throw 'Write-policy telemetry cleanup produced no changes.' }
git commit -m 'refactor(core): remove obsolete write-through telemetry'
git push origin HEAD:main
