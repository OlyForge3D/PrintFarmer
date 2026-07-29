using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

public static class QueueDispatchAttemptResultMapper
{
    public static DispatchAttemptResultDto Map(
        QueueDispatchAttempt attempt,
        PrintJob job,
        string? dispatchStateRevision)
    {
        return new DispatchAttemptResultDto
        {
            AttemptId = attempt.Id,
            AttemptNumber = attempt.AttemptNumber,
            Outcome = attempt.Outcome,
            BackendAcceptedAtUtc = attempt.BackendAcceptedAtUtc,
            ErrorCode = attempt.ErrorCode,
            ErrorDetail = attempt.ErrorDetail,
            IsRetryable = attempt.IsRetryable,
            RequiresReconciliation = attempt.RequiresReconciliation,
            JobRevision = job.RowVersion is { Length: > 0 }
                ? Convert.ToBase64String(job.RowVersion)
                : null,
            DispatchStateRevision = dispatchStateRevision,
        };
    }
}
