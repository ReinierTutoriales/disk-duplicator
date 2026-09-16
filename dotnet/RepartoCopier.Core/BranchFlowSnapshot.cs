namespace RepartoCopier.Core;

public sealed record BranchFlowSnapshot(
    string Destination,
    int IngressQueueDepth,
    long PendingPayloadBytes,
    long PeakPendingPayloadBytes,
    long PhysicalBacklogBytes,
    int OutstandingIo,
    int CurrentQueueDepth,
    bool ReplayAvailable);
