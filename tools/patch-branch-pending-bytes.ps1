$ErrorActionPreference = 'Stop'

$enginePath = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$contractPath = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'
$engine = Get-Content -Raw $enginePath
$contract = Get-Content -Raw $contractPath

function Replace-ExactlyOnce([string]$text, [string]$old, [string]$new, [string]$label) {
    $count = ([regex]::Matches($text, [regex]::Escape($old))).Count
    if ($count -ne 1) { throw "$label expected exactly one match, found $count." }
    return $text.Replace($old, $new)
}

function Replace-ExactlyN([string]$text, [string]$old, [string]$new, [int]$expected, [string]$label) {
    $count = ([regex]::Matches($text, [regex]::Escape($old))).Count
    if ($count -ne $expected) { throw "$label expected exactly $expected matches, found $count." }
    return $text.Replace($old, $new)
}

$engine = Replace-ExactlyOnce $engine @'
                var backlogOwned = false;
                var queueOwned = false;
                var blockOwned = true;
'@ @'
                var backlogOwned = false;
                var pendingPayloadOwned = false;
                var queueOwned = false;
                var blockOwned = true;
'@ 'delivery ownership locals'

$engine = Replace-ExactlyOnce $engine @'
                    worker.DeviceScheduler.ReserveBacklog(message.Block.Length);
                    backlogOwned = true;

                    if (!worker.IsActive)
'@ @'
                    worker.DeviceScheduler.ReserveBacklog(message.Block.Length);
                    backlogOwned = true;
                    worker.ReservePendingPayload(message.Block.Length);
                    pendingPayloadOwned = true;

                    if (!worker.IsActive)
'@ 'branch pending reservation'

$engine = Replace-ExactlyOnce $engine @'
                        worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                        backlogOwned = false;
                        message.Block.Release();
                        blockOwned = false;
'@ @'
                        worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                        backlogOwned = false;
                        worker.ReleasePendingPayload(message.Block.Length);
                        pendingPayloadOwned = false;
                        message.Block.Release();
                        blockOwned = false;
'@ 'inactive branch release'

$engine = Replace-ExactlyOnce $engine @'
                    if (worker.Channel.Writer.TryWrite(message))
                    {
                        queueOwned = false;
                        backlogOwned = false;
                        blockOwned = false;
                        continue;
                    }
'@ @'
                    if (worker.Channel.Writer.TryWrite(message))
                    {
                        queueOwned = false;
                        backlogOwned = false;
                        pendingPayloadOwned = false;
                        blockOwned = false;
                        continue;
                    }
'@ 'delivery ownership transfer'

$engine = Replace-ExactlyOnce $engine @'
                    worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                    backlogOwned = false;
                    message.Block.Release();
                    blockOwned = false;
'@ @'
                    worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                    backlogOwned = false;
                    worker.ReleasePendingPayload(message.Block.Length);
                    pendingPayloadOwned = false;
                    message.Block.Release();
                    blockOwned = false;
'@ 'closed channel branch release'

$engine = Replace-ExactlyOnce $engine @'
                    if (backlogOwned)
                        worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                    if (blockOwned)
                        message.Block.Release();
'@ @'
                    if (backlogOwned)
                        worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                    if (pendingPayloadOwned)
                        worker.ReleasePendingPayload(message.Block.Length);
                    if (blockOwned)
                        message.Block.Release();
'@ 'delivery exception release'

$engine = Replace-ExactlyOnce $engine @'
                    if (dataOwnedByWriter)
                        data?.Block.Release();
                    controlDelivery?.ReleaseBudget();
'@ @'
                    if (dataOwnedByWriter && data is not null)
                        ReleaseBranchBlock(worker, data.Block);
                    controlDelivery?.ReleaseBudget();
'@ 'writer unscheduled release'

$engine = Replace-ExactlyOnce $engine @'
                await ReleasePendingWritesAsync(current).ConfigureAwait(false);
'@ @'
                await ReleasePendingWritesAsync(worker, current).ConfigureAwait(false);
'@ 'pending cleanup worker argument'

$engine = Replace-ExactlyN $engine @'
                worker.NoteProgress();
                block.Release();
                return PendingWriteResult.Success();
'@ @'
                worker.NoteProgress();
                ReleaseBranchBlock(worker, block);
                return PendingWriteResult.Success();
'@ 2 'successful branch release'

$engine = Replace-ExactlyOnce $engine @'
            catch (Exception ex)
            {
                block.Release();
                return PendingWriteResult.Failed(ex);
            }
'@ @'
            catch (Exception ex)
            {
                ReleaseBranchBlock(worker, block);
                return PendingWriteResult.Failed(ex);
            }
'@ 'direct failure branch release'

$engine = Replace-ExactlyOnce $engine @'
        block.Release();
        return PendingWriteResult.Failed(
'@ @'
        ReleaseBranchBlock(worker, block);
        return PendingWriteResult.Failed(
'@ 'buffered terminal branch release'

$engine = Replace-ExactlyOnce $engine @'
            foreach (var retry in results.Where(result => result.Status == PendingWriteStatus.NeedsBufferedRetry))
                retry.RetryBlock?.Release();
'@ @'
            foreach (var retry in results.Where(result => result.Status == PendingWriteStatus.NeedsBufferedRetry))
                ReleaseRetryBlock(worker, retry.RetryBlock);
'@ 'failed drain retry release'

$engine = Replace-ExactlyOnce $engine @'
            foreach (var retry in fallback)
                retry.RetryBlock?.Release();
'@ @'
            foreach (var retry in fallback)
                ReleaseRetryBlock(worker, retry.RetryBlock);
'@ 'fallback switch failure release'

$engine = Replace-ExactlyOnce $engine @'
    private static async Task ReleasePendingWritesAsync(CurrentFile current)
    {
        if (current.PendingWrites.Count == 0)
            return;
        var pending = current.PendingWrites.ToArray();
        current.PendingWrites.Clear();
        var results = await Task.WhenAll(pending).ConfigureAwait(false);
        foreach (var result in results)
            result.RetryBlock?.Release();
    }
'@ @'
    private static async Task ReleasePendingWritesAsync(DestinationWorker worker, CurrentFile current)
    {
        if (current.PendingWrites.Count == 0)
            return;
        var pending = current.PendingWrites.ToArray();
        current.PendingWrites.Clear();
        var results = await Task.WhenAll(pending).ConfigureAwait(false);
        foreach (var result in results)
            ReleaseRetryBlock(worker, result.RetryBlock);
    }

    private static void ReleaseRetryBlock(DestinationWorker worker, SharedBlock? block)
    {
        if (block is not null)
            ReleaseBranchBlock(worker, block);
    }

    private static void ReleaseBranchBlock(DestinationWorker worker, SharedBlock block)
    {
        worker.ReleasePendingPayload(block.Length);
        block.Release();
    }
'@ 'pending cleanup and branch release helper'

$engine = Replace-ExactlyOnce $engine @'
            if (message is DataMessage data)
                worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
            ReleaseQueuedMessage(message);
'@ @'
            if (message is DataMessage data)
            {
                worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
                ReleaseBranchBlock(worker, data.Block);
            }
            else
            {
                ReleaseQueuedControl(message);
            }
'@ 'drain branch accounting'

$engine = Replace-ExactlyOnce $engine @'
    private static void ReleaseQueuedMessage(FanoutMessage message)
    {
        switch (message)
        {
            case DataMessage data:
                data.Block.Release();
                break;
            case ControlDelivery control:
                control.ReleaseBudget();
                break;
        }
    }
'@ @'
    private static void ReleaseQueuedControl(FanoutMessage message)
    {
        if (message is ControlDelivery control)
            control.ReleaseBudget();
    }
'@ 'queued release helper cleanup'

$engine = Replace-ExactlyOnce $engine @'
        private int _active = 1;
        private int _queueDepth;
        private long _lastProgressTicks = DateTime.UtcNow.Ticks;
'@ @'
        private int _active = 1;
        private int _queueDepth;
        private long _pendingPayloadBytes;
        private long _peakPendingPayloadBytes;
        private long _lastProgressTicks = DateTime.UtcNow.Ticks;
'@ 'worker pending fields'

$engine = Replace-ExactlyOnce $engine @'
        public bool IsActive => Volatile.Read(ref _active) != 0;
        public DateTime LastProgressUtc => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);

        public void NoteProgress() => Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);
'@ @'
        public bool IsActive => Volatile.Read(ref _active) != 0;
        public long PendingPayloadBytes => Interlocked.Read(ref _pendingPayloadBytes);
        public long PeakPendingPayloadBytes => Interlocked.Read(ref _peakPendingPayloadBytes);
        public DateTime LastProgressUtc => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);

        public void NoteProgress() => Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);

        public void ReservePendingPayload(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            var pending = Interlocked.Add(ref _pendingPayloadBytes, bytes);
            var peak = Interlocked.Read(ref _peakPendingPayloadBytes);
            while (pending > peak)
            {
                var observed = Interlocked.CompareExchange(ref _peakPendingPayloadBytes, pending, peak);
                if (observed == peak)
                    break;
                peak = observed;
            }
        }

        public void ReleasePendingPayload(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            while (true)
            {
                var current = Interlocked.Read(ref _pendingPayloadBytes);
                if (current < bytes)
                    throw new InvalidOperationException("La rama intentó liberar más payload FAN-OUT del que mantiene pendiente.");
                if (Interlocked.CompareExchange(ref _pendingPayloadBytes, current - bytes, current) == current)
                    return;
            }
        }
'@ 'worker pending accounting methods'

$contract = Replace-ExactlyOnce $contract @'
        CollectionAssert.Contains(engineMethods, "ReleaseQueuedMessage");
'@ @'
        CollectionAssert.Contains(engineMethods, "ReleaseQueuedControl");
        CollectionAssert.DoesNotContain(engineMethods, "ReleaseQueuedMessage");
'@ 'control-plane release contract migration'

$anchor = @'
    [TestMethod]
    public void DestinationWriterKeepsMultipleBlocksInFlightWithExplicitOffsets()
'@
$test = @'
    [TestMethod]
    public void DestinationBranchTracksPayloadUntilItsSharedReferenceIsActuallyReleased()
    {
        var worker = typeof(CopyEngine).GetNestedType("DestinationWorker", BindingFlags.NonPublic);
        Assert.IsNotNull(worker);

        var properties = worker
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.Contains(properties, "PendingPayloadBytes");
        CollectionAssert.Contains(properties, "PeakPendingPayloadBytes");

        var methods = worker
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.Contains(methods, "ReservePendingPayload");
        CollectionAssert.Contains(methods, "ReleasePendingPayload");

        var engineMethods = typeof(CopyEngine)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.Contains(engineMethods, "ReleaseBranchBlock");
        CollectionAssert.Contains(engineMethods, "ReleaseQueuedControl");
        CollectionAssert.DoesNotContain(engineMethods, "ReleaseQueuedMessage");
    }

'@
if ($contract -notmatch 'DestinationBranchTracksPayloadUntilItsSharedReferenceIsActuallyReleased') {
    $contract = Replace-ExactlyOnce $contract $anchor ($test + $anchor) 'branch pending contract anchor'
}

if ($engine -match 'ReleaseQueuedMessage') { throw 'Obsolete ReleaseQueuedMessage helper remains.' }
if (($engine | Select-String -Pattern 'worker\.NoteProgress\(\);\s+block\.Release\(\);\s+return PendingWriteResult\.Success\(\);' -AllMatches).Matches.Count -ne 0) {
    throw 'A successful writer path still releases SharedBlock without branch pending-byte accounting.'
}

Set-Content -Path $enginePath -Value $engine -NoNewline
Set-Content -Path $contractPath -Value $contract -NoNewline
