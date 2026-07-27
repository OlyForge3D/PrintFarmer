namespace Farm.Infrastructure.Services.SignalR;

/// <summary>
/// Versioned envelope for authenticated SignalR queue events.
///
/// <para>
/// Identity and ordering are DURABLE: <see cref="EventId"/> and <see cref="OccurredAtUtc"/>
/// come from the persisted outbox row (set once at write time) and <see cref="Sequence"/> is
/// the cross-process monotonic outbox sequence. A redelivery therefore carries the SAME
/// identity and position, so consumers can de-duplicate and detect gaps.
/// </para>
/// </summary>
/// <param name="SchemaVersion">Envelope schema version.</param>
/// <param name="EventId">Durable outbox event identity — stable across redeliveries.</param>
/// <param name="Sequence">Durable monotonic outbox sequence for gap detection and cursor reads.</param>
/// <param name="EventType">Fully-qualified event type name.</param>
/// <param name="OccurredAtUtc">Durable UTC write timestamp — stable across redeliveries.</param>
/// <param name="JobId">Aggregate print job, when the event is job-scoped.</param>
/// <param name="PrinterId">Printer the event relates to, when applicable.</param>
/// <param name="ProjectId">Project the event relates to, when applicable.</param>
/// <param name="JobStatus">Print job status at write time.</param>
/// <param name="JobKind">Print job kind at write time.</param>
/// <param name="JobRevision">Base-64 job row version at write time.</param>
/// <param name="DispatchStateRevision">Base-64 printer dispatch-state row version at write time.</param>
/// <param name="AttemptId">Dispatch attempt this event belongs to, when applicable.</param>
/// <param name="BedClearState">Bed-clear acknowledgement state at write time.</param>
/// <param name="ErrorCode">Legacy typed error code (kept for wire compatibility).</param>
/// <param name="FailureCode">Typed failure code for terminal/failure events.</param>
/// <param name="PayloadJson">Redacted payload — public identifiers only.</param>
/// <param name="JobLogicalRevision">Resulting provider-independent job revision.</param>
/// <param name="DispatchStateLogicalRevision">Resulting provider-independent dispatch revision.</param>
public sealed record QueueEventEnvelope(
    string SchemaVersion,
    Guid EventId,
    long Sequence,
    string EventType,
    DateTime OccurredAtUtc,
    Guid? JobId,
    Guid? PrinterId,
    Guid? ProjectId,
    string? JobStatus,
    string? JobKind,
    string? JobRevision,
    string? DispatchStateRevision,
    Guid? AttemptId,
    string? BedClearState,
    string? ErrorCode,
    string? FailureCode,
    string? PayloadJson,
    long? JobLogicalRevision,
    long? DispatchStateLogicalRevision)
{
    /// <summary>
    /// Creates an envelope with a DURABLE identity taken from the persisted outbox row.
    /// Callers must pass the stored event id, sequence and creation timestamp so redeliveries
    /// are byte-identical and de-duplicable.
    /// </summary>
    /// <param name="eventId">Durable outbox row id.</param>
    /// <param name="sequence">Durable outbox sequence.</param>
    /// <param name="occurredAtUtc">Durable outbox creation timestamp.</param>
    /// <param name="eventType">Fully-qualified event type name.</param>
    /// <param name="jobId">Aggregate print job.</param>
    /// <param name="printerId">Printer the event relates to.</param>
    /// <param name="projectId">Calibration or print project the event relates to.</param>
    /// <param name="jobStatus">Print job status at write time.</param>
    /// <param name="jobKind">Print job kind at write time.</param>
    /// <param name="jobRevision">Job row version at write time.</param>
    /// <param name="dispatchStateRevision">Dispatch-state row version at write time.</param>
    /// <param name="attemptId">Dispatch attempt id.</param>
    /// <param name="bedClearState">Bed-clear acknowledgement state.</param>
    /// <param name="errorCode">Legacy typed error code.</param>
    /// <param name="failureCode">Typed failure code.</param>
    /// <param name="payloadJson">Redacted payload.</param>
    /// <param name="jobLogicalRevision">Provider-independent resulting job revision.</param>
    /// <param name="dispatchStateLogicalRevision">Provider-independent resulting dispatch revision.</param>
    /// <returns>A durable, de-duplicable envelope.</returns>
    public static QueueEventEnvelope FromOutbox(
        Guid eventId,
        long sequence,
        DateTime occurredAtUtc,
        string eventType,
        Guid? jobId = null,
        Guid? printerId = null,
        Guid? projectId = null,
        string? jobStatus = null,
        string? jobKind = null,
        byte[]? jobRevision = null,
        byte[]? dispatchStateRevision = null,
        Guid? attemptId = null,
        string? bedClearState = null,
        string? errorCode = null,
        string? failureCode = null,
        string? payloadJson = null,
        long? jobLogicalRevision = null,
        long? dispatchStateLogicalRevision = null) =>
        new(
            "2",
            eventId,
            sequence,
            eventType,
            occurredAtUtc,
            jobId,
            printerId,
            projectId,
            jobStatus,
            jobKind,
            Encode(jobRevision),
            Encode(dispatchStateRevision),
            attemptId,
            bedClearState,
            errorCode,
            failureCode,
            payloadJson,
            jobLogicalRevision,
            dispatchStateLogicalRevision);

    /// <summary>
    /// Produces a printer-view hint without job, project, attempt, revision, failure, or
    /// calibration payload data. Authorized job/project subscribers receive the full envelope.
    /// </summary>
    public QueueEventEnvelope RedactForPrinter() =>
        this with
        {
            EventType = "PrintFarmer.Queue.PrinterStateChanged.v1",
            JobId = null,
            ProjectId = null,
            JobKind = null,
            JobRevision = null,
            DispatchStateRevision = null,
            AttemptId = null,
            BedClearState = null,
            ErrorCode = null,
            FailureCode = null,
            PayloadJson = null,
            JobLogicalRevision = null,
            DispatchStateLogicalRevision = null,
        };

    private static string? Encode(byte[]? rowVersion) =>
        rowVersion is { Length: > 0 } ? Convert.ToBase64String(rowVersion) : null;
}
