using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Canonical validity predicate for pending exact-job bed-clear commands.
/// </summary>
internal static class BedClearCommandValidity
{
    /// <summary>
    /// Determines whether a pending command still matches every authoritative dispatch fence.
    /// </summary>
    public static bool IsCurrent(
        BedClearCommandRecord command,
        PrintJob job,
        PrinterDispatchState dispatchState,
        Guid? currentQueueHeadId,
        long? currentPrinterConfigRevision,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(dispatchState);

        return command.Status == BedClearCommandStatus.Pending &&
               job.JobKind == JobKind.FilamentCalibration &&
               job.Status is PrintJobStatus.Queued or PrintJobStatus.Assigned &&
               job.AssignedPrinterId == command.PrinterId &&
               dispatchState.PrinterId == command.PrinterId &&
               dispatchState.AcknowledgedJobId == job.Id &&
               string.Equals(
                   dispatchState.AcknowledgementIdempotencyKey,
                   command.IdempotencyKey,
                   StringComparison.Ordinal) &&
               command.ExpiresAtUtc > utcNow &&
               dispatchState.AcknowledgementExpiresAtUtc > utcNow &&
               command.QueueRevision == dispatchState.QueueRevision &&
               dispatchState.AcknowledgedQueueRevision == dispatchState.QueueRevision &&
               command.PrinterConfigRevision == currentPrinterConfigRevision &&
               dispatchState.AcknowledgedPrinterConfigRevision == currentPrinterConfigRevision &&
               command.JobRowVersion.SequenceEqual(job.RowVersion ?? []) &&
               dispatchState.AcknowledgedJobRowVersion is not null &&
               dispatchState.AcknowledgedJobRowVersion.SequenceEqual(job.RowVersion ?? []) &&
               currentQueueHeadId == job.Id;
    }

    /// <summary>Determines whether either persisted expiry fence has elapsed.</summary>
    public static bool IsExpired(
        BedClearCommandRecord command,
        PrinterDispatchState dispatchState,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(dispatchState);

        return command.ExpiresAtUtc <= utcNow ||
               dispatchState.AcknowledgementExpiresAtUtc <= utcNow;
    }
}
