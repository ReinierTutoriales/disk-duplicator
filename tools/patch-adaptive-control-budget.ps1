$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Get-Content $Path -Raw
    if (-not $text.Contains($Old)) { throw "Expected block not found in $Path" }
    $text = $text.Replace($Old, $New)
    Set-Content $Path $text -NoNewline
}

$engine = 'dotnet/RepartoCopier.Core/CopyEngine.cs'

Replace-Exact $engine @'
        using var resources = new ResourceGovernor();
        var bufferBudget = AdaptiveByteBudget.CreateForSystem();
        var pipeline = new PipelineGovernor(bufferBudget, BlockSize);
'@ @'
        using var resources = new ResourceGovernor();
        var bufferBudget = AdaptiveByteBudget.CreateForSystem();
        var controlBudget = AdaptiveControlByteBudget.CreateForSystem();
        var pipeline = new PipelineGovernor(bufferBudget, BlockSize);
'@

Replace-Exact $engine @'
                    progress[index],
                    copy.DestinationDevices[index],
                    deviceSchedulers.For(copy.DestinationDevices[index])))
'@ @'
                    progress[index],
                    copy.DestinationDevices[index],
                    deviceSchedulers.For(copy.DestinationDevices[index]),
                    controlBudget))
'@

$oldDeliver = @'
    private static async ValueTask DeliverControlAsync(
        DestinationWorker worker,
        ControlMessage message,
        CopyJob job)
    {
        if (!worker.IsActive)
            return;

        job.Token.ThrowIfCancellationRequested();
        await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
        if (!worker.IsActive)
            return;

        worker.IncrementQueueDepth();
        if (worker.Channel.Writer.TryWrite(message))
            return;

        worker.DecrementQueueDepth();
        if (worker.IsActive)
            worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
    }
'@
$newDeliver = @'
    private static async ValueTask DeliverControlAsync(
        DestinationWorker worker,
        ControlMessage message,
        CopyJob job)
    {
        if (!worker.IsActive)
            return;

        job.Token.ThrowIfCancellationRequested();
        await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
        if (!worker.IsActive)
            return;

        var reservationBytes = AdaptiveControlByteBudget.EstimatedDeliveryBytes;
        await worker.ControlBudget.AcquireAsync(reservationBytes, job.Token).ConfigureAwait(false);
        var delivery = new ControlDelivery(message, worker.ControlBudget, reservationBytes);
        var queueOwned = false;
        try
        {
            if (!worker.IsActive)
                return;

            worker.IncrementQueueDepth();
            queueOwned = true;
            if (worker.Channel.Writer.TryWrite(delivery))
            {
                delivery = null;
                queueOwned = false;
                return;
            }

            worker.DecrementQueueDepth();
            queueOwned = false;
            if (worker.IsActive)
                worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
        }
        finally
        {
            if (queueOwned)
                worker.DecrementQueueDepth();
            delivery?.ReleaseBudget();
        }
    }
'@
Replace-Exact $engine $oldDeliver $newDeliver

$oldWriterStart = @'
            await foreach (var message in worker.Channel.Reader.ReadAllAsync())
            {
                worker.DecrementQueueDepth();
                var data = message as DataMessage;
                if (data is not null)
                    worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
                try
                {
                    if (!worker.IsActive)
                        continue;

                    job.Token.ThrowIfCancellationRequested();
                    await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
                    switch (message)
'@
$newWriterStart = @'
            await foreach (var message in worker.Channel.Reader.ReadAllAsync())
            {
                worker.DecrementQueueDepth();
                var controlDelivery = message as ControlDelivery;
                var effectiveMessage = controlDelivery?.Message ?? message;
                var data = effectiveMessage as DataMessage;
                if (data is not null)
                    worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
                try
                {
                    if (!worker.IsActive)
                        continue;

                    job.Token.ThrowIfCancellationRequested();
                    await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
                    switch (effectiveMessage)
'@
Replace-Exact $engine $oldWriterStart $newWriterStart

Replace-Exact $engine @'
                finally
                {
                    data?.Block.Release();
                }
'@ @'
                finally
                {
                    data?.Block.Release();
                    controlDelivery?.ReleaseBudget();
                }
'@

$oldDrain = @'
    private static void DrainAndRelease(DestinationWorker worker)
    {
        while (worker.Channel.Reader.TryRead(out var message))
        {
            worker.DecrementQueueDepth();
            if (message is DataMessage data)
                worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
            ReleaseIfData(message);
        }
    }

    private static void ReleaseIfData(FanoutMessage message)
    {
        if (message is DataMessage data) data.Block.Release();
    }
'@
$newDrain = @'
    private static void DrainAndRelease(DestinationWorker worker)
    {
        while (worker.Channel.Reader.TryRead(out var message))
        {
            worker.DecrementQueueDepth();
            if (message is DataMessage data)
                worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
            ReleaseQueuedMessage(message);
        }
    }

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
'@
Replace-Exact $engine $oldDrain $newDrain

$oldMessages = @'
    private abstract record FanoutMessage;
    private abstract record ControlMessage : FanoutMessage;
    private sealed record BeginMessage(FileEntry Entry) : ControlMessage;
    private sealed record DataMessage(SharedBlock Block) : FanoutMessage;
    private sealed record EndMessage(byte[] Hash) : ControlMessage;
'@
$newMessages = @'
    private abstract record FanoutMessage;
    private abstract record ControlMessage : FanoutMessage;
    private sealed record BeginMessage(FileEntry Entry) : ControlMessage;
    private sealed record DataMessage(SharedBlock Block) : FanoutMessage;
    private sealed record EndMessage(byte[] Hash) : ControlMessage;

    private sealed class ControlDelivery : FanoutMessage
    {
        private AdaptiveControlByteBudget? _budget;
        private readonly int _reservedBytes;

        internal ControlDelivery(
            ControlMessage message,
            AdaptiveControlByteBudget budget,
            int reservedBytes)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            _budget = budget ?? throw new ArgumentNullException(nameof(budget));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reservedBytes);
            _reservedBytes = reservedBytes;
        }

        internal ControlMessage Message { get; }

        internal void ReleaseBudget()
        {
            var budget = Interlocked.Exchange(ref _budget, null);
            budget?.Release(_reservedBytes);
        }
    }
'@
Replace-Exact $engine $oldMessages $newMessages

$oldCtor = @'
        public DestinationWorker(
            string root,
            int slot,
            DestinationProgress progress,
            StorageDeviceInfo device,
            DeviceScheduler deviceScheduler)
        {
            Root = root;
            Slot = slot;
            Progress = progress;
            Device = device;
            DeviceScheduler = deviceScheduler;
            Channel = System.Threading.Channels.Channel.CreateUnbounded<FanoutMessage>(new UnboundedChannelOptions
'@
$newCtor = @'
        public DestinationWorker(
            string root,
            int slot,
            DestinationProgress progress,
            StorageDeviceInfo device,
            DeviceScheduler deviceScheduler,
            AdaptiveControlByteBudget controlBudget)
        {
            Root = root;
            Slot = slot;
            Progress = progress;
            Device = device;
            DeviceScheduler = deviceScheduler;
            ControlBudget = controlBudget ?? throw new ArgumentNullException(nameof(controlBudget));
            Channel = System.Threading.Channels.Channel.CreateUnbounded<FanoutMessage>(new UnboundedChannelOptions
'@
Replace-Exact $engine $oldCtor $newCtor

Replace-Exact $engine @'
        public StorageDeviceInfo Device { get; }
        public DeviceScheduler DeviceScheduler { get; }
        public Channel<FanoutMessage> Channel { get; }
'@ @'
        public StorageDeviceInfo Device { get; }
        public DeviceScheduler DeviceScheduler { get; }
        internal AdaptiveControlByteBudget ControlBudget { get; }
        public Channel<FanoutMessage> Channel { get; }
'@

Write-Host 'Adaptive control-plane budget migration applied.'
