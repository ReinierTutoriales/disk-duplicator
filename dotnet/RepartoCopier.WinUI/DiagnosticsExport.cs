using System.Text.Json;
using System.Text.Json.Serialization;
using RepartoCopier.Core;

namespace RepartoCopier.WinUI;

internal sealed record DiagnosticsDocument(
    int SchemaVersion,
    string? ApplicationVersion,
    string? BuildRevision,
    string? BuildInformationalVersion,
    DateTimeOffset? StartedAt,
    CopyDiagnosticsSnapshot? Diagnostics,
    IReadOnlyList<DestinationSnapshot> Destinations,
    IReadOnlyList<string> MeasurementNotes,
    bool? VerificationRequested,
    bool? IndependentSourceReads);

internal static class DiagnosticsExport
{
    // 2: adds BuildRevision/BuildInformationalVersion and, per destination, device identification,
    // WriteTime, durable flush counts/times, phase marks, CopyFinishedAt/VerifyFinishedAt, durations and Outcome.
    // 3: adds VerificationRequested to distinguish copied from verified jobs.
    // 4: records the independent source-read COPY mode used for performance comparisons.
    internal const int SchemaVersion = 4;

    internal static IReadOnlyList<string> MeasurementNotes { get; } =
    [
        "Durations are TimeSpan strings; byte rates use bytes per second.",
        "Diagnostics.BufferWaitTime measures the complete RentAsync call, including immediate rentals; it is not pure blocking time.",
        "Diagnostics.SourceReadTime includes source scheduler acquisition when a device is shared.",
        "Diagnostics.WriteTime, flush, commit and recovery times are summed across concurrent destinations; they are not COPY wall time.",
        "Destinations[].WriteTime sums only that destination's successful write calls, using the same measurement as Diagnostics.WriteTime. It includes waiting for the per-device I/O gate. Direct writes also include retry delays inside one call; buffered writes record the successful attempt only, excluding earlier failed attempts and retry delays. It excludes flush, commit, file open/close and time spent waiting for data.",
        "Destinations[].DurableFlushes counts successful durable flushes; DurableFlushTime sums their elapsed time and uses the same boundaries as Diagnostics.DurableFlushTime. Direct flush timing includes FinalizeLength. Failed flush attempts are not recorded.",
        "Destinations[].CopyStartedAt, CopyFinishedAt, VerifyStartedAt, VerifyFinishedAt and PhaseMarks are offsets from the creation of that destination's progress tracker, which happens after preflight and before the writers start. They are not offsets from Diagnostics.CopyPhaseElapsed.",
        "Destinations[].TrackingStartedAt is the UTC wall-clock anchor for these monotonic offsets; adding an offset yields the approximate event timestamp. Durations use Stopwatch and are unaffected by later wall-clock adjustments.",
        "CopyFinishedAt is recorded only when the destination was still copying, no cancellation was requested and every file was finished, skipped or reported as an error. VerifyFinishedAt is recorded only when every verify file of a destination that was still verifying completed. Failed or cancelled destinations keep that state in Outcome and PhaseMarks and never receive a completion mark afterwards.",
        "Verification reads the source and all destinations per block, so VerifyFinishedAt values are expected to be close to each other; this build did not change that schedule.",
        "Destinations[].Outcome is Completed, CompletedWithErrors, Failed, Cancelled or InProgress, derived from the final Phase and FilesErrored.",
        "BuildRevision is the commit SHA embedded at build time (SourceRevisionId) and is null when the build had none.",
        "This final snapshot does not record a time series.",
        "IndependentSourceReads records whether COPY used one source read per destination and one bounded pool per destination. If true, SourceReadBytes and SourceHashBytes are physical aggregate work across producers and can be up to N times logical bytes. BufferWaitTime sums waits across producers and cannot be divided by COPY wall time to infer one producer's stall.",
        "ComparisonBytesRead counts actual destination bytes read when comparing existing content; source reads are not included. ComparisonBytesProcessed counts logical candidate bytes classified, including unread tails once a difference is found. ComparisonFilesDone and ComparisonIdenticalFiles count candidates classified and identical candidates. Comparison precedes COPY and is excluded from CopyPhaseElapsed and final verification counters.",
        "VerificationRequested records the final full-content reread choice (null if unknown). Outcome=Completed means the requested operation completed, not that verification ran. VerifyFinishedAt and verification counters record actual verification. Without verification, source hashing, write checks, durable flush and atomic commit still run; destination content is not checked by final reread.",
    ];

    internal static DiagnosticsDocument Create(
        string? applicationVersion,
        string? informationalVersion,
        DateTimeOffset? startedAt,
        CopyDiagnosticsSnapshot? diagnostics,
        IReadOnlyList<DestinationSnapshot> destinations,
        bool? verificationRequested = null,
        bool? independentSourceReads = null) =>
        new(
            SchemaVersion,
            applicationVersion,
            BuildInfo.Revision(informationalVersion),
            informationalVersion,
            startedAt,
            diagnostics,
            destinations,
            MeasurementNotes,
            verificationRequested,
            independentSourceReads);

    internal static string Serialize(DiagnosticsDocument document)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(document, options);
    }
}
