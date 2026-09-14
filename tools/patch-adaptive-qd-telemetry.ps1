$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New, [string]$Label) {
    $text = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) { throw "${Label}: exact pattern not found" }
    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$path = 'dotnet/RepartoCopier.Core/DeviceScheduler.cs'

Replace-Exact $path @'
    int QueueDepthUpshifts,
    int QueueDepthDownshifts,
    long BacklogTargetBytes,
'@ @'
    int QueueDepthUpshifts,
    int QueueDepthDownshifts,
    string LastQueueDepthDecision,
    double BestObservedThroughputBytesPerSecond,
    double BestObservedAverageLatencyMilliseconds,
    long BacklogTargetBytes,
'@ 'snapshot adaptive telemetry shape'

Replace-Exact $path @'
    private int _queueDepthUpshifts;
    private int _queueDepthDownshifts;
    private int _outstandingIo;
'@ @'
    private int _queueDepthUpshifts;
    private int _queueDepthDownshifts;
    private string _lastQueueDepthDecision = "initial";
    private int _outstandingIo;
'@ 'scheduler decision field'

Replace-Exact $path @'
                _queueDepthUpshifts,
                _queueDepthDownshifts,
                BacklogTargetBytes,
'@ @'
                _queueDepthUpshifts,
                _queueDepthDownshifts,
                _lastQueueDepthDecision,
                _bestThroughputBytesPerSecond,
                double.IsFinite(_bestAverageLatencySeconds) ? _bestAverageLatencySeconds * 1000.0 : 0.0,
                BacklogTargetBytes,
'@ 'snapshot adaptive telemetry values'

Replace-Exact $path @'
        var decisionInterval = Math.Max(8, Math.Min(256, _currentQueueDepth * 2));
'@ @'
        var decisionInterval = Math.Max(8, (int)Math.Min(256L, (long)_currentQueueDepth * 2));
'@ 'overflow-safe decision interval'

Replace-Exact $path @'
        if (!sawDemand)
            return;

        if (_bestThroughputBytesPerSecond <= 0)
        {
            ObserveBestLocked(throughput, averageLatency);
            UpshiftLocked();
            return;
        }
'@ @'
        if (!sawDemand)
        {
            _lastQueueDepthDecision = "hold:no-demand";
            return;
        }

        if (_bestThroughputBytesPerSecond <= 0)
        {
            ObserveBestLocked(throughput, averageLatency);
            UpshiftLocked("increase:baseline-demand");
            return;
        }
'@ 'baseline decision reason'

Replace-Exact $path @'
                ObserveBestLocked(throughput, averageLatency);
                UpshiftLocked();
                return;
'@ @'
                ObserveBestLocked(throughput, averageLatency);
                UpshiftLocked("increase:throughput");
                return;
'@ 'throughput upshift reason'

Replace-Exact $path @'
                DownshiftToBestLocked();
                return;
'@ @'
                DownshiftToBestLocked("decrease:regression");
                return;
'@ 'downshift reason'

Replace-Exact $path @'
            ObserveBestLocked(Math.Max(throughput, _bestThroughputBytesPerSecond),
                Math.Min(averageLatency, _bestAverageLatencySeconds));
            UpshiftLocked();
            return;
'@ @'
            ObserveBestLocked(Math.Max(throughput, _bestThroughputBytesPerSecond),
                Math.Min(averageLatency, _bestAverageLatencySeconds));
            UpshiftLocked("increase:competitive");
            return;
'@ 'competitive upshift reason'

Replace-Exact $path @'
        if (throughput >= _bestThroughputBytesPerSecond * 0.995)
            ObserveBestLocked(Math.Max(throughput, _bestThroughputBytesPerSecond),
                Math.Min(averageLatency, _bestAverageLatencySeconds));
        UpshiftLocked();
'@ @'
        if (throughput >= _bestThroughputBytesPerSecond * 0.995)
            ObserveBestLocked(Math.Max(throughput, _bestThroughputBytesPerSecond),
                Math.Min(averageLatency, _bestAverageLatencySeconds));
        UpshiftLocked("increase:demand");
'@ 'demand upshift reason'

Replace-Exact $path @'
    private void UpshiftLocked()
    {
        var next = NextExplorationDepth(_currentQueueDepth);
        if (next <= _currentQueueDepth)
            return;
        _currentQueueDepth = next;
        _queueDepthUpshifts++;
'@ @'
    private void UpshiftLocked(string reason)
    {
        var next = NextExplorationDepth(_currentQueueDepth);
        if (next <= _currentQueueDepth)
        {
            _lastQueueDepthDecision = "hold:integer-limit";
            return;
        }
        _currentQueueDepth = next;
        _queueDepthUpshifts++;
        _lastQueueDepthDecision = reason;
'@ 'upshift decision method'

Replace-Exact $path @'
    private void DownshiftToBestLocked()
    {
        var next = Math.Max(1, _bestObservedQueueDepth);
        if (next >= _currentQueueDepth)
            return;
        _currentQueueDepth = next;
        _queueDepthDownshifts++;
'@ @'
    private void DownshiftToBestLocked(string reason)
    {
        var next = Math.Max(1, _bestObservedQueueDepth);
        if (next >= _currentQueueDepth)
        {
            _lastQueueDepthDecision = "hold:best-current";
            return;
        }
        _currentQueueDepth = next;
        _queueDepthDownshifts++;
        _lastQueueDepthDecision = reason;
'@ 'downshift decision method'

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $path
if (git diff --cached --quiet) { throw 'Adaptive QD telemetry closure produced no changes.' }
git commit -m 'perf(core): expose adaptive QD decision telemetry'
git push origin HEAD:main
