namespace Farm.Infrastructure.Services.SignalR;

/// <summary>Versioned envelope for authenticated SignalR queue events.</summary>
public sealed record QueueEventEnvelope(
    string SchemaVersion,
    Guid EventId,
    string EventType,
    DateTime OccurredAtUtc,
    Guid? JobId,
    Guid? PrinterId,
    string? JobStatus,
    string? JobKind,
    string? ErrorCode,
    string? PayloadJson)
{
    public static QueueEventEnvelope Create(
        string eventType,
        Guid? jobId = null,
        Guid? printerId = null,
        string? jobStatus = null,
        string? jobKind = null,
        string? errorCode = null,
        string? payloadJson = null) =>
        new("1", Guid.NewGuid(), eventType, DateTime.UtcNow, jobId, printerId, jobStatus, jobKind, errorCode, payloadJson);
}
