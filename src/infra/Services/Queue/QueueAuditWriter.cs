using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Writes durable <see cref="QueueOperationAudit"/> rows into the caller's
/// <see cref="AppDbContext"/> change tracker so the audit commits in the SAME transaction
/// as the operation it records (issue #900, defect 13).
///
/// The writer never calls <c>SaveChangesAsync</c> itself: callers own the transaction so
/// an audit row can neither be lost when the operation commits nor survive a rollback.
/// </summary>
public static class QueueAuditWriter
{
    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Adds an audit row to <paramref name="db"/> without saving.
    /// </summary>
    /// <param name="db">Database context owning the surrounding transaction.</param>
    /// <param name="actorSubject">Operator or system identity performing the operation.</param>
    /// <param name="operation">Typed operation name from <see cref="QueueAuditOperations"/>.</param>
    /// <param name="outcome">Typed outcome from <see cref="QueueAuditOutcomes"/>.</param>
    /// <param name="resourceType">Resource kind (for example <c>PrintJob</c>).</param>
    /// <param name="resourceId">Resource identity.</param>
    /// <param name="printerId">Printer involved, when applicable.</param>
    /// <param name="printJobId">Print job involved, when applicable.</param>
    /// <param name="dispatchAttemptId">Dispatch attempt involved, when applicable.</param>
    /// <param name="reasonCode">Typed failure/deny reason code.</param>
    /// <param name="jobRowVersion">Job revision observed at commit time.</param>
    /// <param name="dispatchStateRowVersion">Dispatch-state revision observed at commit time.</param>
    /// <param name="idempotencyKey">Idempotency key associated with the operation.</param>
    /// <param name="detail">Redacted structured detail (identifiers and typed codes only).</param>
    /// <returns>The audit row that was added to the change tracker.</returns>
    public static QueueOperationAudit Add(
        AppDbContext db,
        string actorSubject,
        string operation,
        string outcome,
        string resourceType,
        Guid? resourceId = null,
        Guid? printerId = null,
        Guid? printJobId = null,
        Guid? dispatchAttemptId = null,
        string? reasonCode = null,
        byte[]? jobRowVersion = null,
        byte[]? dispatchStateRowVersion = null,
        string? idempotencyKey = null,
        object? detail = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        var row = new QueueOperationAudit
        {
            Id = Guid.NewGuid(),
            OccurredAtUtc = DateTime.UtcNow,
            ActorSubject = Truncate(string.IsNullOrWhiteSpace(actorSubject) ? "system" : actorSubject, 256),
            Operation = Truncate(operation, 64),
            Outcome = Truncate(outcome, 32),
            ResourceType = Truncate(resourceType, 64),
            ResourceId = resourceId,
            PrinterId = printerId,
            PrintJobId = printJobId,
            DispatchAttemptId = dispatchAttemptId,
            ReasonCode = reasonCode is null ? null : Truncate(reasonCode, 128),
            JobRowVersion = jobRowVersion,
            DispatchStateRowVersion = dispatchStateRowVersion,
            IdempotencyKey = idempotencyKey is null ? null : Truncate(idempotencyKey, 512),
            DetailJson = detail is null ? null : Truncate(JsonSerializer.Serialize(detail, DetailOptions), 2048),
        };

        _ = db.QueueOperationAudits.Add(row);
        return row;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
