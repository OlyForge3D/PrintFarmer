using System.Security.Cryptography;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Services.Webhooks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.AutoDispatch;

/// <summary>
/// Interface for the auto-dispatch ready-gate service.
/// Manages the workflow where a printer waits for operator confirmation
/// before dispatching the next queued job.
/// </summary>
public interface IAutoDispatchService
{
    /// <summary>
    /// Transitions a printer to PendingReady state after a job completes.
    /// Called by PrintJobCompletionService when a print finishes on an auto-dispatch-enabled printer.
    /// </summary>
    Task TransitionToPendingReadyAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Marks the printer as ready. Returns the next queued job if available,
    /// along with a filament pre-flight check result.
    /// </summary>
    Task<AutoDispatchReadyResult> MarkReadyAsync(Guid printerId, CancellationToken ct = default);

    Task<AutoDispatchReadyResult> MarkReadyAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        CancellationToken ct = default);

    Task<AutoDispatchReadyResult> MarkReadyAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        bool confirmFilamentOverride,
        string actorSubject,
        byte[]? expectedOverrideJobVersion,
        CancellationToken ct = default);

    Task<AutoDispatchReadyResult> MarkReadyAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        bool confirmFilamentOverride,
        string actorSubject,
        byte[]? expectedOverrideJobVersion,
        byte[]? expectedFilamentCheckVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Skips the next queued job (cancels it) and remains in PendingReady state
    /// if more jobs are queued, or transitions to None if the queue is empty.
    /// </summary>
    Task<AutoDispatchStatusDto> SkipNextJobAsync(Guid printerId, CancellationToken ct = default);

    Task<AutoDispatchStatusDto> SkipNextJobAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        byte[] expectedJobVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels the auto-dispatch ready-gate workflow and returns the printer to None state.
    /// </summary>
    Task<AutoDispatchStatusDto> CancelAutoAsync(Guid printerId, CancellationToken ct = default);

    Task<AutoDispatchStatusDto> CancelAutoAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Pre-confirms that the printer bed is clear, allowing immediate job dispatch
    /// when the next job is queued without waiting for PendingReady confirmation.
    /// </summary>
    Task<AutoDispatchStatusDto> MarkPreClearAsync(Guid printerId, CancellationToken ct = default);

    Task<AutoDispatchStatusDto> MarkPreClearAsync(
        Guid printerId,
        string actorSubject,
        CancellationToken ct = default) =>
        MarkPreClearAsync(printerId, ct);

    Task<AutoDispatchStatusDto> MarkPreClearAsync(
        Guid printerId,
        string actorSubject,
        byte[] expectedDispatchStateVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the current auto-dispatch status for a printer.
    /// </summary>
    Task<AutoDispatchStatusDto> GetStatusAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Enables or disables auto-dispatch for a printer.
    /// </summary>
    Task<AutoDispatchStatusDto> SetEnabledAsync(Guid printerId, bool enabled, CancellationToken ct = default);

    Task<AutoDispatchStatusDto> SetEnabledAsync(
        Guid printerId,
        bool enabled,
        byte[] expectedDispatchStateVersion,
        byte[] expectedPrinterVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Gets auto-dispatch status for all printers, wrapped with global enabled state.
    /// </summary>
    Task<AutoDispatchGlobalStatusDto> GetAllStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Enables or disables auto-dispatch for all printers at once.
    /// </summary>
    Task<List<AutoDispatchStatusDto>> SetAllEnabledAsync(bool enabled, CancellationToken ct = default);

    Task<List<AutoDispatchStatusDto>> SetAllEnabledAsync(
        bool enabled,
        IReadOnlyDictionary<Guid, AutoDispatchExpectedVersions> expectedVersions,
        CancellationToken ct = default);
}

public sealed record AutoDispatchExpectedVersions(
    byte[] DispatchStateVersion,
    byte[] PrinterVersion);

/// <summary>
/// Result of marking a printer as ready in the auto-dispatch ready-gate workflow.
/// </summary>
public class AutoDispatchReadyResult
{
    public AutoDispatchStatusDto Status { get; set; } = new();

    /// <summary>
    /// The next queued job that will be dispatched, if any.
    /// </summary>
    public NextJobDto? NextJob { get; set; }

    /// <summary>
    /// Result of the filament pre-flight check.
    /// </summary>
    public FilamentCheckResult? FilamentCheck { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the server initiated dispatch for the next job.
    /// </summary>
    public bool DispatchInitiated { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether explicit filament override confirmation is required.
    /// </summary>
    public bool RequiresFilamentOverride { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this request applied an explicit filament override.
    /// </summary>
    public bool FilamentOverrideApplied { get; set; }

    /// <summary>
    /// Opaque revision of the exact filament outcome and details shown to the operator.
    /// </summary>
    public string? FilamentCheckETag { get; set; }

    /// <summary>
    /// Indicates that filament conditions changed after the operator reviewed the warning.
    /// </summary>
    public bool FilamentCheckChanged { get; set; }

    /// <summary>
    /// Typed physical dispatch outcome reported by the durable attempt.
    /// </summary>
    public string? DispatchOutcome { get; set; }

    /// <summary>
    /// Indicates that the physical start may have occurred and requires reconciliation.
    /// </summary>
    public bool DispatchReconciliationPending { get; set; }
}

public class NextJobDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public double? EstimatedFilamentUsageG { get; set; }

    public string? RequiredMaterialType { get; set; }

    public TimeSpan? EstimatedPrintTime { get; set; }

    public string JobKind { get; set; } =
        nameof(Farm.Infrastructure.Domain.JobKind.Standard);

    public string? JobETag { get; set; }

    public long? ExpectedPrinterConfigRevision { get; set; }
}

public class FilamentCheckResult
{
    public FilamentCheckOutcome Outcome { get; set; }

    public bool Sufficient { get; set; }

    public double? RemainingWeightG { get; set; }

    public double? RequiredWeightG { get; set; }

    public string? LoadedMaterial { get; set; }

    public string? RequiredMaterial { get; set; }

    public bool MaterialMismatch { get; set; }

    public string? Message { get; set; }
}

public enum FilamentCheckOutcome
{
    Unknown = 0,
    Incompatible = 1,
    Compatible = 2,
}

public class AutoDispatchStatusDto
{
    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public bool IsReady { get; set; }

    public string? CurrentJobName { get; set; }

    public int QueueDepth { get; set; }

    public List<ReadyGateCheckDto> ReadyGateChecks { get; set; } = [];

    public string? LastActivity { get; set; }

    public string State { get; set; } = "None";

    public bool BedPreConfirmed { get; set; }

    public string? DispatchStateETag { get; set; }

    public string? PrinterETag { get; set; }

    public Guid? NextJobId { get; set; }

    /// <summary>
    /// Redacted (null) when <see cref="NextJobId"/> refers to an unassigned queued job that has
    /// merely scored as a dispatch candidate for this printer but has no ownership/permission
    /// relationship to this printer's authorized viewers. Populated once the job is actually
    /// assigned to this printer.
    /// </summary>
    public string? NextJobName { get; set; }

    public string? NextJobETag { get; set; }

    /// <summary>Redacted for unassigned candidate jobs — see <see cref="NextJobName"/>.</summary>
    public string? NextJobKind { get; set; }

    /// <summary>Redacted for unassigned candidate jobs — see <see cref="NextJobName"/>.</summary>
    public long? NextJobPrinterConfigRevision { get; set; }

    public string? AttentionMessage { get; set; }
}

public class ReadyGateCheckDto
{
    public string Name { get; set; } = string.Empty;

    public bool Passed { get; set; }

    public string Message { get; set; } = string.Empty;

    public string CheckedAt { get; set; } = DateTime.UtcNow.ToString("o");
}

public class AutoDispatchGlobalStatusDto
{
    public bool GlobalEnabled { get; set; }

    public List<AutoDispatchStatusDto> Printers { get; set; } = [];
}

public class AutoDispatchService(
    AppDbContext db,
    IHubContext<PrinterHub> hub,
    ILogger<AutoDispatchService> logger,
    ISpoolmanService? spoolmanService = null,
    IWebhookService? webhookService = null,
    Queue.Dispatch.IAutoDispatchTrigger? dispatchTrigger = null,
    IDispatchScorer? dispatchScorer = null,
    IFilamentCoverageBroadcaster? coverageBroadcaster = null,
    IJobDispatchService? jobDispatchService = null) : IAutoDispatchService
{
    private const string ReadyGateLogPrefix = "[AutoDispatchReadyGate]";
    private const string AutoDispatchStateChangedEventName = "autodispatchstatechanged";
    private const string AutoDispatchReadyWebhookEventName = "printer.autodispatch_ready";
    private const string AutoDispatchPendingWebhookEventName = "printer.autodispatch_pending";

    private sealed record QueuedJobSelection(PrintJob? NextJob, int QueueDepth);

    /// <summary>
    /// Pre-computed scoring context for the unassigned-queued-job pool, shared across every
    /// printer's eligibility check in a single request. Each unassigned job is scored exactly
    /// once (via <see cref="IDispatchScorer.ScorePrintersForJobAsync"/>, which already scores
    /// every enabled printer per call), instead of once per (printer, job) pair.
    /// </summary>
    private sealed record UnassignedJobScoringContext(
        List<PrintJob> UnassignedJobs,
        Dictionary<Guid, Dictionary<Guid, DispatchScore>> ScoresByJobId,
        double MinimumScoreThreshold);

    private sealed record OccupyingJobSelection(
        Guid PrinterId,
        string? Name,
        PrintJobStatus Status,
        DateTime SortTime);

    private sealed record EffectiveAutoDispatchState(
        AutoDispatchState WorkflowState,
        string ReportedState);

    /// <summary>
    /// Returns the existing DispatchState or creates a new one lazily on first write.
    /// </summary>
    private static PrinterDispatchState EnsureDispatchState(Printer printer)
    {
        if (printer.DispatchState is null)
        {
            printer.DispatchState = new PrinterDispatchState { PrinterId = printer.Id };
        }

        return printer.DispatchState;
    }

    public async Task TransitionToPendingReadyAsync(Guid printerId, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers.Include(p => p.DispatchState).FirstOrDefaultAsync(p => p.Id == printerId, ct);
        if (printer is null)
        {
            logger.LogWarning(ReadyGateLogPrefix + " Printer {PrinterId} not found for PendingReady transition", printerId);
            return;
        }

        if (!printer.AutoDispatchEnabled)
        {
            logger.LogDebug(ReadyGateLogPrefix + " Auto-dispatch not enabled for printer {PrinterId}, skipping", printerId);
            return;
        }

        // A printer-occupying job always wins over ready-gate workflow state.
        bool hasOccupyingJob = await db.PrintJobs
            .WhereOccupiesPrinter()
            .AnyAsync(
                j => j.AssignedPrinterId == printerId,
                ct);

        if (hasOccupyingJob)
        {
            logger.LogDebug(ReadyGateLogPrefix + " Printer {PrinterId} has an occupying job — skipping PendingReady transition", printerId);
            return;
        }

        QueuedJobSelection queuedJobs = await GetQueuedJobSelectionAsync(printerId, includeGcodeFile: true, ct);

        if (queuedJobs.QueueDepth == 0)
        {
            logger.LogDebug(ReadyGateLogPrefix + " No queued jobs for printer {PrinterId}, staying in None state", printerId);
            PrinterDispatchState ds = EnsureDispatchState(printer);
            ds.AutoDispatchState = AutoDispatchState.None;
            ds.BedPreConfirmed = false; // Reset pre-clear flag
            await db.SaveChangesAsync(ct);
            return;
        }

        // A pre-clear is printer-scoped only until completion. At completion,
        // re-evaluate and dispatch the exact current queue head.
        if (printer.DispatchState?.BedPreConfirmed == true)
        {
            PrintJob reviewedJob = queuedJobs.NextJob!;
            FilamentCheckResult filamentCheck =
                await CheckFilamentAsync(printer, reviewedJob, ct);
            if (filamentCheck.Outcome != FilamentCheckOutcome.Compatible)
            {
                logger.LogWarning(
                    ReadyGateLogPrefix + " Pre-cleared printer {PrinterId} requires explicit filament confirmation for job {JobId}: {Outcome}: {FilamentCheckMessage}",
                    printerId,
                    reviewedJob.Id,
                    filamentCheck.Outcome,
                    filamentCheck.Message);
                await RequirePendingReadyConfirmationAsync(printer, ct);
                return;
            }

            if (jobDispatchService is null ||
                reviewedJob.RowVersion is null ||
                printer.RowVersion is null ||
                printer.DispatchState.RowVersion is null)
            {
                logger.LogWarning(
                    ReadyGateLogPrefix + " Exact pre-cleared dispatch is unavailable for printer {PrinterId}; requiring operator confirmation",
                    printerId);
                await RequirePendingReadyConfirmationAsync(printer, ct);
                return;
            }

            byte[] filamentCheckVersion =
                FilamentPreflightEvaluator.ComputeVersion(filamentCheck);
            FilamentOverrideAuthorization review = new(
                filamentCheck.Outcome.ToString(),
                filamentCheck.Message ?? "Filament is compatible.",
                filamentCheck.LoadedMaterial,
                filamentCheck.RequiredMaterial,
                filamentCheck.RemainingWeightG,
                filamentCheck.RequiredWeightG,
                filamentCheckVersion,
                printer.RowVersion.ToArray(),
                OverrideApproved: false);
            try
            {
                QueuedPrintJobDto dispatched =
                    await jobDispatchService.DispatchReviewedJobAsync(
                        reviewedJob.Id,
                        printerId,
                        QueueActorIdentity.AutoDispatch,
                        Convert.ToBase64String(reviewedJob.RowVersion),
                        printer.DispatchState.RowVersion,
                        review,
                        ct);
                DispatchAttemptOutcome? outcome =
                    dispatched.DispatchResult?.Outcome;
                if (outcome is not (
                        DispatchAttemptOutcome.Accepted or
                        DispatchAttemptOutcome.Unknown))
                {
                    logger.LogWarning(
                        ReadyGateLogPrefix + " Exact pre-cleared dispatch was not accepted for printer {PrinterId}, job {JobId}: {Outcome}",
                        printerId,
                        reviewedJob.Id,
                        outcome?.ToString() ?? "Unavailable");
                    await RequirePendingReadyConfirmationAsync(printer, ct);
                    return;
                }

                AutoDispatchStatusDto dispatchedStatus =
                    await BuildStatusDtoAsync(printer, ct);
                await hub.Clients.Group(
                        AuthorizedHubGroups.Printer(printerId))
                    .SendAsync(
                        AutoDispatchStateChangedEventName,
                        dispatchedStatus,
                        ct);
                if (outcome == DispatchAttemptOutcome.Accepted)
                {
                    webhookService?.Enqueue(
                        AutoDispatchReadyWebhookEventName,
                        new { printerId, printerName = printer.Name });
                }
            }
            catch (FilamentCheckChangedException ex)
            {
                logger.LogWarning(
                    ReadyGateLogPrefix + " Filament evidence changed before exact pre-cleared dispatch for printer {PrinterId}, job {JobId}: {Outcome}: {FilamentCheckMessage}",
                    printerId,
                    reviewedJob.Id,
                    ex.CurrentCheck.Outcome,
                    ex.CurrentCheck.Message);
                await RequirePendingReadyConfirmationAsync(printer, ct);
            }

            return;
        }

        EnsureDispatchState(printer).AutoDispatchState = AutoDispatchState.PendingReady;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(ReadyGateLogPrefix + " Printer {PrinterId} ({Name}) transitioned to PendingReady", printerId, printer.Name);

        // Broadcast state change via SignalR
        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.Group(AuthorizedHubGroups.Printer(printerId)).SendAsync(AutoDispatchStateChangedEventName, status, ct);

        webhookService?.Enqueue(AutoDispatchPendingWebhookEventName, new { printerId, printerName = printer.Name });
    }

    private async Task RequirePendingReadyConfirmationAsync(
        Printer printer,
        CancellationToken ct)
    {
        Printer currentPrinter = await db.Printers
            .Include(candidate => candidate.DispatchState)
            .SingleAsync(candidate => candidate.Id == printer.Id, ct);
        PrinterDispatchState blockedState =
            EnsureDispatchState(currentPrinter);
        blockedState.AutoDispatchState = AutoDispatchState.PendingReady;
        blockedState.BedPreConfirmed = false;
        await db.SaveChangesAsync(ct);

        AutoDispatchStatusDto blockedStatus =
            await BuildStatusDtoAsync(currentPrinter, ct);
        await hub.Clients.Group(
                AuthorizedHubGroups.Printer(currentPrinter.Id))
            .SendAsync(
                AutoDispatchStateChangedEventName,
                blockedStatus,
                ct);
        webhookService?.Enqueue(
            AutoDispatchPendingWebhookEventName,
            new
            {
                printerId = currentPrinter.Id,
                printerName = currentPrinter.Name,
            });
    }

    public Task<AutoDispatchReadyResult> MarkReadyAsync(
        Guid printerId,
        CancellationToken ct = default) =>
        MarkReadyCoreAsync(
            printerId,
            expectedDispatchStateVersion: null,
            confirmFilamentOverride: false,
            QueueActorIdentity.AutoDispatch,
            expectedOverrideJobVersion: null,
            expectedFilamentCheckVersion: null,
            ct);

    public Task<AutoDispatchReadyResult> MarkReadyAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        CancellationToken ct = default) =>
        MarkReadyCoreAsync(
            printerId,
            expectedDispatchStateVersion,
            confirmFilamentOverride: false,
            QueueActorIdentity.AutoDispatch,
            expectedOverrideJobVersion: null,
            expectedFilamentCheckVersion: null,
            ct);

    public Task<AutoDispatchReadyResult> MarkReadyAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        bool confirmFilamentOverride,
        string actorSubject,
        byte[]? expectedOverrideJobVersion,
        CancellationToken ct = default) =>
        MarkReadyAsync(
            printerId,
            expectedDispatchStateVersion,
            confirmFilamentOverride,
            actorSubject,
            expectedOverrideJobVersion,
            expectedFilamentCheckVersion: null,
            ct);

    public Task<AutoDispatchReadyResult> MarkReadyAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        bool confirmFilamentOverride,
        string actorSubject,
        byte[]? expectedOverrideJobVersion,
        byte[]? expectedFilamentCheckVersion,
        CancellationToken ct = default) =>
        MarkReadyCoreAsync(
            printerId,
            expectedDispatchStateVersion,
            confirmFilamentOverride,
            actorSubject,
            expectedOverrideJobVersion,
            expectedFilamentCheckVersion,
            ct);

    private async Task<AutoDispatchReadyResult> MarkReadyCoreAsync(
        Guid printerId,
        byte[]? expectedDispatchStateVersion,
        bool confirmFilamentOverride,
        string actorSubject,
        byte[]? expectedOverrideJobVersion,
        byte[]? expectedFilamentCheckVersion,
        CancellationToken ct)
    {
        Printer? printer = await db.Printers.Include(p => p.DispatchState).FirstOrDefaultAsync(p => p.Id == printerId, ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        BindDispatchStateVersion(printer.DispatchState, expectedDispatchStateVersion);

        if (!printer.AutoDispatchEnabled)
        {
            throw new InvalidOperationException($"Auto-dispatch is not enabled for printer {printer.Name}");
        }

        QueuedJobSelection queuedJobs = await GetQueuedJobSelectionAsync(printerId, includeGcodeFile: true, ct);
        PrintJobStatus? currentJobStatus = await db.PrintJobs
            .WhereOccupiesPrinter()
            .Where(j => j.AssignedPrinterId == printerId)
            .Select(j => (PrintJobStatus?)j.Status)
            .FirstOrDefaultAsync(ct);
        EffectiveAutoDispatchState effectiveState = ResolveEffectiveState(
            printer,
            queuedJobs.QueueDepth,
            currentJobStatus);
        bool hasReadyConfirmation = currentJobStatus?.OccupiesPrinter() != true
            && (effectiveState.WorkflowState == AutoDispatchState.PendingReady
                || effectiveState.WorkflowState == AutoDispatchState.Ready
                || (printer.DispatchState?.BedPreConfirmed ?? false));

        if (!hasReadyConfirmation)
        {
            throw new InvalidOperationException($"Printer {printer.Name} is not in PendingReady state (current: {printer.DispatchState?.AutoDispatchState ?? AutoDispatchState.None})");
        }

        PrintJob? nextJob = queuedJobs.NextJob;

        if (nextJob is null)
        {
            // No more queued jobs — return to None
            PrinterDispatchState dsEmpty = EnsureDispatchState(printer);
            dsEmpty.AutoDispatchState = AutoDispatchState.None;
            dsEmpty.BedPreConfirmed = false;
            await db.SaveChangesAsync(ct);

            var emptyStatus = await BuildStatusDtoAsync(printer, ct);
            await hub.Clients.Group(AuthorizedHubGroups.Printer(printerId)).SendAsync(AutoDispatchStateChangedEventName, emptyStatus, ct);

            return new AutoDispatchReadyResult
            {
                Status = emptyStatus,
                NextJob = null,
                FilamentCheck = new FilamentCheckResult
                {
                    Outcome = FilamentCheckOutcome.Compatible,
                    Sufficient = true,
                    Message = "No queued jobs remaining",
                },
            };
        }

        if (nextJob.JobKind == JobKind.FilamentCalibration)
        {
            throw new InvalidOperationException(
                "Calibration jobs require the exact-job acknowledge-bed-clear-and-start endpoint.");
        }

        if (confirmFilamentOverride &&
            (expectedOverrideJobVersion is null ||
             nextJob.RowVersion is null ||
             !expectedOverrideJobVersion.SequenceEqual(nextJob.RowVersion)))
        {
            throw new QueueRevisionConflictException(
                "The reviewed queue head changed before the filament override was confirmed.",
                nextJob.RowVersion,
                printer.DispatchState?.RowVersion);
        }

        // Perform filament pre-flight check
        FilamentCheckResult filamentCheck = await CheckFilamentAsync(printer, nextJob, ct);

        // nextJob may be an unassigned candidate merely scored as dispatch-eligible for this
        // printer (see #1324) rather than a job this printer's operator has any ownership of.
        // Redact identifying fields until the job is actually assigned/dispatched to printerId.
        bool nextJobAssignedToPrinter = nextJob.AssignedPrinterId == printerId;
        NextJobDto nextJobDto = BuildNextJobDto(nextJob, nextJobAssignedToPrinter);
        bool filamentOverrideRequired =
            filamentCheck.Outcome != FilamentCheckOutcome.Compatible;
        byte[] filamentCheckVersion =
            FilamentPreflightEvaluator.ComputeVersion(filamentCheck);
        string filamentCheckETag = Convert.ToBase64String(filamentCheckVersion);

        if (confirmFilamentOverride &&
            (expectedFilamentCheckVersion is null ||
             !CryptographicOperations.FixedTimeEquals(
                 expectedFilamentCheckVersion,
                 filamentCheckVersion)))
        {
            AutoDispatchStatusDto changedStatus = await BuildStatusDtoAsync(printer, ct);
            logger.LogInformation(
                ReadyGateLogPrefix + " Filament conditions changed before confirmation for printer {PrinterId}, job {JobId}; requiring review of {Outcome}: {FilamentCheckMessage}",
                printerId,
                nextJob.Id,
                filamentCheck.Outcome,
                filamentCheck.Message);
            return new AutoDispatchReadyResult
            {
                Status = changedStatus,
                NextJob = nextJobDto,
                FilamentCheck = filamentCheck,
                FilamentCheckETag = filamentCheckETag,
                DispatchInitiated = false,
                RequiresFilamentOverride = filamentOverrideRequired,
                FilamentCheckChanged = true,
            };
        }

        if (filamentOverrideRequired && !confirmFilamentOverride)
        {
            AutoDispatchStatusDto blockedStatus = await BuildStatusDtoAsync(printer, ct);
            logger.LogInformation(
                ReadyGateLogPrefix + " Dispatch blocked for printer {PrinterId}, job {JobId}: filament check {Outcome}: {FilamentCheckMessage}",
                printerId,
                nextJob.Id,
                filamentCheck.Outcome,
                filamentCheck.Message);

            return new AutoDispatchReadyResult
            {
                Status = blockedStatus,
                NextJob = nextJobDto,
                FilamentCheck = filamentCheck,
                FilamentCheckETag = filamentCheckETag,
                DispatchInitiated = false,
                RequiresFilamentOverride = true,
            };
        }

        if (jobDispatchService is null)
        {
            throw new InvalidOperationException(
                "Exact-job ready dispatch is unavailable.");
        }

        if (filamentOverrideRequired)
        {
            logger.LogWarning(
                ReadyGateLogPrefix + " Operator {ActorSubject} explicitly overrode {Outcome} filament check for printer {PrinterId}, job {JobId}: {FilamentCheckMessage}",
                actorSubject,
                filamentCheck.Outcome,
                printerId,
                nextJob.Id,
                filamentCheck.Message);
        }

        string authorizationReason = filamentCheck.Message ??
            "Filament compatibility could not be verified.";
        byte[] reviewedPrinterVersion =
            printer.RowVersion?.ToArray() ??
            throw new InvalidOperationException(
                "The reviewed printer revision is required for ready dispatch.");
        FilamentOverrideAuthorization authorization = new(
            filamentCheck.Outcome.ToString(),
            authorizationReason,
            filamentCheck.LoadedMaterial,
            filamentCheck.RequiredMaterial,
            filamentCheck.RemainingWeightG,
            filamentCheck.RequiredWeightG,
            filamentCheckVersion,
            reviewedPrinterVersion,
            OverrideApproved: filamentOverrideRequired);

        byte[] reviewedDispatchStateVersion = expectedDispatchStateVersion ??
            printer.DispatchState?.RowVersion ??
            throw new InvalidOperationException(
                "The reviewed dispatch-state revision is required for ready dispatch.");
        string reviewedJobETag = Convert.ToBase64String(
            filamentOverrideRequired
                ? expectedOverrideJobVersion!
                : nextJob.RowVersion ??
                  throw new InvalidOperationException(
                      "The reviewed job revision is required for ready dispatch."));
        QueuedPrintJobDto dispatched;
        try
        {
            dispatched = await jobDispatchService.DispatchReviewedJobAsync(
                nextJob.Id,
                printerId,
                actorSubject,
                reviewedJobETag,
                reviewedDispatchStateVersion,
                authorization,
                ct);
        }
        catch (FilamentCheckChangedException ex)
        {
            Printer currentPrinter = await db.Printers
                .Include(candidate => candidate.DispatchState)
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == printerId, ct);
            AutoDispatchStatusDto changedStatus =
                await BuildStatusDtoAsync(currentPrinter, ct);
            nextJobDto.JobETag = changedStatus.NextJobId == nextJob.Id
                ? changedStatus.NextJobETag
                : null;
            return new AutoDispatchReadyResult
            {
                Status = changedStatus,
                NextJob = nextJobDto,
                FilamentCheck = ex.CurrentCheck,
                FilamentCheckETag =
                    Convert.ToBase64String(ex.CurrentCheckVersion),
                DispatchInitiated = false,
                RequiresFilamentOverride =
                    ex.CurrentCheck.Outcome !=
                    FilamentCheckOutcome.Compatible,
                FilamentCheckChanged = true,
            };
        }

        DispatchAttemptOutcome? dispatchOutcome =
            dispatched.DispatchResult?.Outcome;
        if (dispatchOutcome is not (
                DispatchAttemptOutcome.Accepted or
                DispatchAttemptOutcome.Unknown))
        {
            string outcome =
                dispatchOutcome?.ToString() ?? "Unavailable";
            throw new InvalidOperationException(
                $"The ready dispatch was not accepted by the printer (outcome: {outcome}).");
        }

        AutoDispatchStatusDto status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.Group(AuthorizedHubGroups.Printer(printerId))
            .SendAsync(AutoDispatchStateChangedEventName, status, ct);

        // The job has now genuinely been dispatched to printerId, so the caller (who is
        // authorized for this printer) is entitled to see full details regardless of
        // whether the job started out unassigned.
        NextJobDto dispatchedNextJobDto = BuildNextJobDto(nextJob, jobAssignedToPrinter: true);
        return new AutoDispatchReadyResult
        {
            Status = status,
            NextJob = dispatchedNextJobDto,
            FilamentCheck = filamentCheck,
            FilamentCheckETag = filamentCheckETag,
            DispatchInitiated = true,
            FilamentOverrideApplied = authorization.OverrideApproved,
            DispatchOutcome = dispatchOutcome.ToString(),
            DispatchReconciliationPending =
                dispatchOutcome == DispatchAttemptOutcome.Unknown,
        };
    }

    public Task<AutoDispatchStatusDto> SkipNextJobAsync(
        Guid printerId,
        CancellationToken ct = default) =>
        SkipNextJobCoreAsync(
            printerId,
            expectedDispatchStateVersion: null,
            expectedJobVersion: null,
            ct);

    public Task<AutoDispatchStatusDto> SkipNextJobAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        byte[] expectedJobVersion,
        CancellationToken ct = default) =>
        SkipNextJobCoreAsync(
            printerId,
            expectedDispatchStateVersion,
            expectedJobVersion,
            ct);

    private async Task<AutoDispatchStatusDto> SkipNextJobCoreAsync(
        Guid printerId,
        byte[]? expectedDispatchStateVersion,
        byte[]? expectedJobVersion,
        CancellationToken ct)
    {
        Printer? printer = await db.Printers.Include(p => p.DispatchState).FirstOrDefaultAsync(p => p.Id == printerId, ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        BindDispatchStateVersion(printer.DispatchState, expectedDispatchStateVersion);

        // Find and cancel the next queued job, using the SINGLE shared ordering selector
        // (Urgent first — an ascending sort would cancel the LOWEST-priority job).
        PrintJob? nextJob = await db.PrintJobs
            .Where(j => j.AssignedPrinterId == printerId && j.Status == PrintJobStatus.Queued)
            .OrderByPriorityDescending()
            .FirstOrDefaultAsync(ct);

        if (nextJob != null)
        {
            BindJobVersion(nextJob, expectedJobVersion);
            nextJob.Status = PrintJobStatus.Cancelled;
            nextJob.UpdatedAt = DateTime.UtcNow;
            logger.LogInformation(
                ReadyGateLogPrefix + " Skipped (cancelled) job {JobId} ({JobName}) for printer {PrinterId}",
                nextJob.Id, nextJob.Name, printerId);
        }

        // Exclude the tracked job being cancelled so the job and dispatch
        // state can be persisted atomically below.
        Guid? skippedJobId = nextJob?.Id;
        bool hasMoreJobs = await db.PrintJobs
            .AnyAsync(
                j => j.AssignedPrinterId == printerId
                        && j.Status == PrintJobStatus.Queued
                        && (!skippedJobId.HasValue || j.Id != skippedJobId.Value), ct);

        EnsureDispatchState(printer).AutoDispatchState = hasMoreJobs ? AutoDispatchState.PendingReady : AutoDispatchState.None;
        await db.SaveChangesAsync(ct);
        if (nextJob is not null && coverageBroadcaster is not null)
        {
            await coverageBroadcaster.BroadcastPrinterChangedAsync(
                printerId,
                FilamentCoverageChangeReasons.QueueChanged,
                ct).ConfigureAwait(false);
        }

        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.Group(AuthorizedHubGroups.Printer(printerId)).SendAsync(AutoDispatchStateChangedEventName, status, ct);

        return status;
    }

    public Task<AutoDispatchStatusDto> CancelAutoAsync(
        Guid printerId,
        CancellationToken ct = default) =>
        CancelAutoCoreAsync(printerId, expectedDispatchStateVersion: null, ct);

    public Task<AutoDispatchStatusDto> CancelAutoAsync(
        Guid printerId,
        byte[] expectedDispatchStateVersion,
        CancellationToken ct = default) =>
        CancelAutoCoreAsync(printerId, expectedDispatchStateVersion, ct);

    private async Task<AutoDispatchStatusDto> CancelAutoCoreAsync(
        Guid printerId,
        byte[]? expectedDispatchStateVersion,
        CancellationToken ct)
    {
        Printer? printer = await db.Printers.Include(p => p.DispatchState).FirstOrDefaultAsync(p => p.Id == printerId, ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        BindDispatchStateVersion(printer.DispatchState, expectedDispatchStateVersion);

        EnsureDispatchState(printer).AutoDispatchState = AutoDispatchState.Dismissed;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(ReadyGateLogPrefix + " Auto-dispatch ready gate cancelled for printer {PrinterId} ({Name})", printerId, printer.Name);

        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.Group(AuthorizedHubGroups.Printer(printerId)).SendAsync(AutoDispatchStateChangedEventName, status, ct);

        return status;
    }

    public async Task<AutoDispatchStatusDto> MarkPreClearAsync(
        Guid printerId,
        CancellationToken ct = default) =>
        await MarkPreClearAsync(
            printerId,
            QueueActorIdentity.AutoDispatch,
            ct);

    public async Task<AutoDispatchStatusDto> MarkPreClearAsync(
        Guid printerId,
        string actorSubject,
        CancellationToken ct = default) =>
        await MarkPreClearCoreAsync(
            printerId,
            actorSubject,
            expectedDispatchStateVersion: null,
            ct);

    public Task<AutoDispatchStatusDto> MarkPreClearAsync(
        Guid printerId,
        string actorSubject,
        byte[] expectedDispatchStateVersion,
        CancellationToken ct = default) =>
        MarkPreClearCoreAsync(
            printerId,
            actorSubject,
            expectedDispatchStateVersion,
            ct);

    private async Task<AutoDispatchStatusDto> MarkPreClearCoreAsync(
        Guid printerId,
        string actorSubject,
        byte[]? expectedDispatchStateVersion,
        CancellationToken ct)
    {
        Printer? printer = await db.Printers.Include(p => p.DispatchState).FirstOrDefaultAsync(p => p.Id == printerId, ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        BindDispatchStateVersion(printer.DispatchState, expectedDispatchStateVersion);

        if (!printer.AutoDispatchEnabled)
        {
            throw new InvalidOperationException($"Auto-dispatch is not enabled for printer {printer.Name}");
        }

        // Guard: printer must be physically unoccupied.
        bool hasOccupyingJob = await db.PrintJobs
            .WhereOccupiesPrinter()
            .AnyAsync(
                j => j.AssignedPrinterId == printerId,
                ct);

        if (hasOccupyingJob)
        {
            throw new InvalidOperationException(
                $"Cannot pre-clear the bed while a job occupies printer {printer.Name}");
        }

        QueuedJobSelection queuedJobs = await GetQueuedJobSelectionAsync(printerId, includeGcodeFile: true, ct);
        if (queuedJobs.NextJob?.JobKind == JobKind.FilamentCalibration)
        {
            throw new InvalidOperationException(
                "Calibration jobs cannot use generic bed pre-clear; acknowledge the exact job instead.");
        }

        PrinterDispatchState preClearState = EnsureDispatchState(printer);
        FilamentCheckResult? queuedFilamentCheck = queuedJobs.NextJob is null
            ? null
            : await CheckFilamentAsync(printer, queuedJobs.NextJob, ct);
        bool requiresFilamentConfirmation =
            queuedFilamentCheck is not null &&
            queuedFilamentCheck.Outcome != FilamentCheckOutcome.Compatible;

        preClearState.BedPreConfirmed = !requiresFilamentConfirmation;
        if (requiresFilamentConfirmation)
        {
            preClearState.AutoDispatchState = AutoDispatchState.PendingReady;
            logger.LogWarning(
                ReadyGateLogPrefix + " Bed pre-clear for printer {PrinterId} stopped at the filament gate for job {JobId}: {Outcome}: {FilamentCheckMessage}",
                printerId,
                queuedJobs.NextJob!.Id,
                queuedFilamentCheck!.Outcome,
                queuedFilamentCheck.Message);
        }

        // Pre-clearing the bed is a SAFETY OVERRIDE: it lets the next job dispatch without
        // the per-job bed-clear acknowledgement. It must be durably audited in the SAME
        // transaction as the flag it sets (issue #900, defect 13).
        if (!requiresFilamentConfirmation)
        {
            _ = QueueAuditWriter.Add(
                db,
                actorSubject,
                QueueAuditOperations.SafetyOverride,
                QueueAuditOutcomes.Success,
                nameof(Printer),
                resourceId: printerId,
                printerId: printerId,
                reasonCode: "bed_pre_confirmed",
                dispatchStateRowVersion: preClearState.RowVersion,
                detail: new { queueDepth = queuedJobs.QueueDepth });
        }

        await db.SaveChangesAsync(ct);

        if (queuedJobs.QueueDepth > 0 && !requiresFilamentConfirmation)
        {
            // A pre-cleared idle printer just became eligible for immediate dispatch.
            dispatchTrigger?.NotifyJobQueued(printerId);
        }

        var status = await BuildStatusDtoAsync(printer, ct);
        await hub.Clients.Group(AuthorizedHubGroups.Printer(printerId)).SendAsync(AutoDispatchStateChangedEventName, status, ct);

        if (!requiresFilamentConfirmation)
        {
            logger.LogInformation(ReadyGateLogPrefix + " Bed pre-clear confirmed for printer {PrinterId} ({Name})", printerId, printer.Name);
            webhookService?.Enqueue("printer.bed_pre_confirmed", new { printerId, printerName = printer.Name });
        }

        return status;
    }

    public async Task<AutoDispatchStatusDto> GetStatusAsync(Guid printerId, CancellationToken ct = default)
    {
        Printer? printer = await db.Printers
            .Include(p => p.DispatchState)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == printerId, ct);

        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        return await BuildStatusDtoAsync(printer, ct);
    }

    public Task<AutoDispatchStatusDto> SetEnabledAsync(
        Guid printerId,
        bool enabled,
        CancellationToken ct = default) =>
        SetEnabledCoreAsync(
            printerId,
            enabled,
            expectedDispatchStateVersion: null,
            expectedPrinterVersion: null,
            ct);

    public Task<AutoDispatchStatusDto> SetEnabledAsync(
        Guid printerId,
        bool enabled,
        byte[] expectedDispatchStateVersion,
        byte[] expectedPrinterVersion,
        CancellationToken ct = default) =>
        SetEnabledCoreAsync(
            printerId,
            enabled,
            expectedDispatchStateVersion,
            expectedPrinterVersion,
            ct);

    private async Task<AutoDispatchStatusDto> SetEnabledCoreAsync(
        Guid printerId,
        bool enabled,
        byte[]? expectedDispatchStateVersion,
        byte[]? expectedPrinterVersion,
        CancellationToken ct)
    {
        Printer? printer = await db.Printers.Include(p => p.DispatchState).FirstOrDefaultAsync(p => p.Id == printerId, ct);
        if (printer is null)
        {
            throw new InvalidOperationException($"Printer {printerId} not found");
        }

        BindDispatchStateVersion(printer.DispatchState, expectedDispatchStateVersion);
        if (expectedPrinterVersion is not null)
        {
            db.Entry(printer).Property(candidate => candidate.Revision).OriginalValue =
                RevisionETag.Decode(expectedPrinterVersion);
        }

        printer.AutoDispatchEnabled = enabled;
        if (!enabled)
        {
            EnsureDispatchState(printer).AutoDispatchState = AutoDispatchState.None;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            ReadyGateLogPrefix + " Auto-dispatch {Action} for printer {PrinterId} ({Name})",
            enabled ? "enabled" : "disabled", printerId, printer.Name);

        return await BuildStatusDtoAsync(printer, ct);
    }

    public async Task<AutoDispatchGlobalStatusDto> GetAllStatusAsync(CancellationToken ct = default)
    {
        List<Printer> printers = await db.Printers.Include(p => p.DispatchState).AsNoTracking().ToListAsync(ct);
        Dictionary<Guid, OccupyingJobSelection> currentJobs =
            await GetCurrentJobsByPrinterAsync(printers.Select(p => p.Id), ct);

        // Score every unassigned job once, up front (J scorer calls total, not P x J). Each
        // scorer call already returns scores for ALL enabled printers, so the same result set
        // can be reused for every printer's per-printer eligibility check below. A failure here
        // (DB/scorer error) must not abort the whole endpoint: fall back to an empty context so
        // each printer's own try/catch below still reports a degraded-but-present status, matching
        // the pre-refactor per-printer failure isolation.
        UnassignedJobScoringContext scoringContext;
        try
        {
            scoringContext = await BuildUnassignedJobScoringContextAsync(includeGcodeFile: false, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to build unassigned-job scoring context for auto-dispatch status");
            scoringContext = new UnassignedJobScoringContext([], [], MinimumScoreThreshold: 0);
        }

        bool globalEnabled = printers.Any(p => p.AutoDispatchEnabled);
        List<AutoDispatchStatusDto> statuses = [];
        foreach (Printer printer in printers)
        {
            try
            {
                QueuedJobSelection queuedJobs =
                    await GetQueuedJobSelectionAsync(printer.Id, includeGcodeFile: false, scoringContext, ct);
                statuses.Add(BuildStatusDto(
                    printer,
                    queuedJobs.QueueDepth,
                    currentJobs.GetValueOrDefault(printer.Id),
                    queuedJobs.NextJob));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to build auto-dispatch status for printer {PrinterId} ({PrinterName})", printer.Id, printer.Name);
                statuses.Add(BuildStatusDto(printer, queuedJobCount: 0, currentJob: null));
            }
        }

        return new AutoDispatchGlobalStatusDto
        {
            GlobalEnabled = globalEnabled,
            Printers = statuses,
        };
    }

    public async Task<List<AutoDispatchStatusDto>> SetAllEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        return await SetAllEnabledCoreAsync(enabled, expectedVersions: null, ct);
    }

    public Task<List<AutoDispatchStatusDto>> SetAllEnabledAsync(
        bool enabled,
        IReadOnlyDictionary<Guid, AutoDispatchExpectedVersions> expectedVersions,
        CancellationToken ct = default) =>
        SetAllEnabledCoreAsync(enabled, expectedVersions, ct);

    private async Task<List<AutoDispatchStatusDto>> SetAllEnabledCoreAsync(
        bool enabled,
        IReadOnlyDictionary<Guid, AutoDispatchExpectedVersions>? expectedVersions,
        CancellationToken ct)
    {
        List<Printer> printers = await db.Printers.Include(p => p.DispatchState).ToListAsync(ct);
        foreach (Printer printer in printers)
        {
            if (expectedVersions is not null)
            {
                if (!expectedVersions.TryGetValue(printer.Id, out AutoDispatchExpectedVersions? expected))
                {
                    throw new QueuePreconditionRequiredException(
                        $"Expected versions are required for printer {printer.Id}.");
                }

                BindDispatchStateVersion(
                    printer.DispatchState,
                    expected.DispatchStateVersion);
                db.Entry(printer).Property(candidate => candidate.Revision).OriginalValue =
                    RevisionETag.Decode(expected.PrinterVersion);
            }

            printer.AutoDispatchEnabled = enabled;
            if (!enabled)
            {
                EnsureDispatchState(printer).AutoDispatchState = AutoDispatchState.None;
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            ReadyGateLogPrefix + " Auto-dispatch {Action} for ALL {Count} printers",
            enabled ? "enabled" : "disabled",
            printers.Count);

        Dictionary<Guid, OccupyingJobSelection> currentJobs =
            await GetCurrentJobsByPrinterAsync(printers.Select(p => p.Id), ct);
        List<AutoDispatchStatusDto> statuses = [];
        foreach (Printer printer in printers)
        {
            QueuedJobSelection queuedJobs = await GetQueuedJobSelectionAsync(printer.Id, includeGcodeFile: false, ct);
            statuses.Add(BuildStatusDto(
                printer,
                queuedJobs.QueueDepth,
                currentJobs.GetValueOrDefault(printer.Id)));
        }

        return statuses;
    }

    private void BindDispatchStateVersion(
        PrinterDispatchState? state,
        byte[]? expectedVersion)
    {
        if (expectedVersion is null)
        {
            return;
        }

        if (state is null)
        {
            throw new QueueRevisionConflictException(
                "The printer dispatch state no longer exists.");
        }

        db.Entry(state).Property(candidate => candidate.Revision).OriginalValue =
            RevisionETag.Decode(expectedVersion);
    }

    private void BindJobVersion(PrintJob job, byte[]? expectedVersion)
    {
        if (expectedVersion is not null)
        {
            db.Entry(job).Property(candidate => candidate.Revision).OriginalValue =
                RevisionETag.Decode(expectedVersion);
        }
    }

    private static AutoDispatchStatusDto BuildStatusDto(
        Printer printer,
        int queuedJobCount,
        OccupyingJobSelection? currentJob = null,
        PrintJob? nextJob = null)
    {
        string now = DateTime.UtcNow.ToString("o");
        EffectiveAutoDispatchState effectiveState = ResolveEffectiveState(
            printer,
            queuedJobCount,
            currentJob?.Status);
        bool isReady = printer.AutoDispatchEnabled
            && effectiveState.WorkflowState == AutoDispatchState.Ready;
        var gateChecks = BuildReadyGateChecks(
            printer,
            queuedJobCount,
            effectiveState.WorkflowState,
            currentJob?.Status,
            now);
        string? attentionMessage = BuildAttentionMessage(
            printer,
            queuedJobCount,
            effectiveState.WorkflowState,
            currentJob?.Status);

        return new AutoDispatchStatusDto
        {
            PrinterId = printer.Id,
            PrinterName = printer.Name,
            Enabled = printer.AutoDispatchEnabled,
            IsReady = isReady,
            CurrentJobName = currentJob?.Name,
            QueueDepth = queuedJobCount,
            ReadyGateChecks = gateChecks,
            State = effectiveState.ReportedState,
            BedPreConfirmed = printer.DispatchState?.BedPreConfirmed ?? false,
            DispatchStateETag = printer.DispatchState?.RowVersion is { Length: > 0 } rowVersion
                ? Convert.ToBase64String(rowVersion)
                : null,
            PrinterETag = printer.RowVersion is { Length: > 0 } printerRowVersion
                ? Convert.ToBase64String(printerRowVersion)
                : null,
            NextJobId = nextJob?.Id,
            NextJobName = IsAssignedToPrinter(nextJob, printer)
                ? nextJob!.Name ?? nextJob.GcodeFile?.Name
                : null,
            NextJobETag = nextJob?.RowVersion is { Length: > 0 } jobRowVersion
                ? Convert.ToBase64String(jobRowVersion)
                : null,
            NextJobKind = IsAssignedToPrinter(nextJob, printer)
                ? (
                    nextJob!.JobKind ??
                    Farm.Infrastructure.Domain.JobKind.Standard).ToString()
                : null,
            NextJobPrinterConfigRevision = IsAssignedToPrinter(nextJob, printer)
                ? nextJob!.PinnedPrinterConfigRevision
                : null,
            AttentionMessage = attentionMessage,
        };
    }

    /// <summary>
    /// A candidate <c>NextJob</c> may come from the unassigned queue (scored as dispatch-
    /// eligible for this printer) rather than being actually assigned to it. Only an assigned
    /// job has passed the ownership/permission checks implied by the receiving printer's
    /// authorized audience, so name/kind/revision must be redacted for unassigned candidates.
    /// See issue #1324.
    /// </summary>
    private static bool IsAssignedToPrinter(PrintJob? nextJob, Printer printer) =>
        nextJob is not null && nextJob.AssignedPrinterId == printer.Id;

    /// <summary>
    /// Builds the exact-job RPC response DTO for the ready-dispatch workflow. When
    /// <paramref name="jobAssignedToPrinter"/> is false — i.e. the candidate job merely
    /// scored as dispatch-eligible for this printer rather than actually being assigned to
    /// it — identifying fields are redacted for the same reason as <see cref="BuildStatusDto"/>.
    /// See issue #1324.
    /// </summary>
    private static NextJobDto BuildNextJobDto(PrintJob nextJob, bool jobAssignedToPrinter) => new()
    {
        Id = nextJob.Id,
        Name = jobAssignedToPrinter
            ? nextJob.Name ?? nextJob.GcodeFile?.Name ?? "Unknown"
            : string.Empty,
        EstimatedFilamentUsageG = jobAssignedToPrinter ? nextJob.EstimatedFilamentUsage : null,
        RequiredMaterialType = jobAssignedToPrinter ? nextJob.RequiredMaterialType : null,
        EstimatedPrintTime = jobAssignedToPrinter ? nextJob.EstimatedPrintTime : null,
        JobKind = jobAssignedToPrinter
            ? (nextJob.JobKind ?? Farm.Infrastructure.Domain.JobKind.Standard).ToString()
            : string.Empty,
        JobETag = nextJob.RowVersion is { Length: > 0 } jobRowVersion
            ? Convert.ToBase64String(jobRowVersion)
            : null,
        ExpectedPrinterConfigRevision = jobAssignedToPrinter ? nextJob.PinnedPrinterConfigRevision : null,
    };

    private async Task<AutoDispatchStatusDto> BuildStatusDtoAsync(Printer printer, CancellationToken ct)
    {
        QueuedJobSelection queuedJobs = await GetQueuedJobSelectionAsync(printer.Id, includeGcodeFile: false, ct);

        OccupyingJobSelection? currentJob = await db.PrintJobs
            .WhereOccupiesPrinter()
            .Where(j => j.AssignedPrinterId == printer.Id)
            .OrderByDescending(j => j.ActualStartTime ?? j.QueuedAt)
            .Select(j => new OccupyingJobSelection(
                printer.Id,
                j.Name ?? j.GcodeFile!.Name,
                j.Status,
                j.ActualStartTime ?? j.QueuedAt))
            .FirstOrDefaultAsync(ct);

        return BuildStatusDto(
            printer,
            queuedJobs.QueueDepth,
            currentJob,
            queuedJobs.NextJob);
    }

    private static EffectiveAutoDispatchState ResolveEffectiveState(
        Printer printer,
        int queuedJobCount,
        PrintJobStatus? currentJobStatus)
    {
        if (currentJobStatus?.OccupiesPrinter() == true)
        {
            return new EffectiveAutoDispatchState(
                AutoDispatchState.None,
                currentJobStatus.Value.ToString());
        }

        AutoDispatchState storedState = printer.DispatchState?.AutoDispatchState ?? AutoDispatchState.None;
        bool bedPreConfirmed = printer.DispatchState?.BedPreConfirmed ?? false;

        if (storedState == AutoDispatchState.Dismissed)
        {
            return new EffectiveAutoDispatchState(
                AutoDispatchState.None,
                nameof(AutoDispatchState.None));
        }

        if (storedState != AutoDispatchState.None)
        {
            return new EffectiveAutoDispatchState(storedState, storedState.ToString());
        }

        if (!printer.AutoDispatchEnabled
            || bedPreConfirmed
            || queuedJobCount <= 0
            || !printer.IsAvailable
            || printer.InMaintenance)
        {
            return new EffectiveAutoDispatchState(
                AutoDispatchState.None,
                nameof(AutoDispatchState.None));
        }

        return new EffectiveAutoDispatchState(
            AutoDispatchState.PendingReady,
            nameof(AutoDispatchState.PendingReady));
    }

    private static List<ReadyGateCheckDto> BuildReadyGateChecks(
        Printer printer,
        int queuedJobCount,
        AutoDispatchState effectiveState,
        PrintJobStatus? currentJobStatus,
        string checkedAt)
    {
        var checks = new List<ReadyGateCheckDto>
        {
            new()
            {
                Name = "Auto-Dispatch Enabled",
                Passed = printer.AutoDispatchEnabled,
                Message = printer.AutoDispatchEnabled ? "Auto-dispatch is enabled" : "Auto-dispatch is disabled for this printer",
                CheckedAt = checkedAt,
            },
            new()
            {
                Name = "Printer Available",
                Passed = printer.IsAvailable && !printer.InMaintenance,
                Message = printer.InMaintenance
                    ? "Printer is in maintenance mode"
                    : printer.IsAvailable ? "Printer is available" : "Printer is not available",
                CheckedAt = checkedAt,
            },
            new()
            {
                Name = "Jobs in Queue",
                Passed = queuedJobCount > 0,
                Message = queuedJobCount > 0
                    ? $"{queuedJobCount} job{(queuedJobCount == 1 ? string.Empty : "s")} queued"
                    : "No jobs queued for this printer",
                CheckedAt = checkedAt,
            },
        };

        if (printer.AutoDispatchEnabled)
        {
            bool printerOccupied = currentJobStatus?.OccupiesPrinter() == true;
            bool confirmationRequired =
                !printerOccupied && effectiveState == AutoDispatchState.PendingReady;
            checks.Add(new ReadyGateCheckDto
            {
                Name = "Bed Clear Confirmed",
                Passed = !printerOccupied && !confirmationRequired,
                Message = effectiveState switch
                {
                    _ when currentJobStatus == PrintJobStatus.Paused => "Paused job still occupies the printer",
                    _ when printerOccupied => "Active job still occupies the printer",
                    AutoDispatchState.Ready => "Operator confirmed bed is clear",
                    _ when printer.DispatchState?.BedPreConfirmed is true => "Bed pre-cleared for immediate dispatch",
                    AutoDispatchState.PendingReady => "Waiting for operator to confirm bed is clear",
                    _ => "No confirmation needed yet",
                },
                CheckedAt = checkedAt,
            });
        }

        return checks;
    }

    private static string? BuildAttentionMessage(
        Printer printer,
        int queuedJobCount,
        AutoDispatchState effectiveState,
        PrintJobStatus? currentJobStatus)
    {
        if (!printer.AutoDispatchEnabled || currentJobStatus?.OccupiesPrinter() == true)
        {
            return null;
        }

        string queuedJobLabel = $"{queuedJobCount} queued job{(queuedJobCount == 1 ? string.Empty : "s")}";

        if (printer.InMaintenance)
        {
            return queuedJobCount > 0
                ? $"Printer is in maintenance mode. {queuedJobLabel} will not start until maintenance is complete and the printer is available."
                : "Printer is in maintenance mode. Complete maintenance and make the printer available before auto-dispatch can resume.";
        }

        if (!printer.IsAvailable)
        {
            return queuedJobCount > 0
                ? $"Printer is unavailable. {queuedJobLabel} will not start until the printer is available again."
                : "Printer is unavailable. Restore printer availability before auto-dispatch can resume.";
        }

        if (effectiveState == AutoDispatchState.PendingReady)
        {
            return queuedJobCount switch
            {
                <= 0 => "Print completed. Clear the bed and confirm ready before queued work can resume.",
                1 => "Print completed. 1 queued job is blocked until you clear the bed and confirm ready. Once confirmed, the next queued job will start automatically.",
                _ => $"Print completed. {queuedJobLabel} are blocked until you clear the bed and confirm ready. Once confirmed, the next queued job will start automatically.",
            };
        }

        if (queuedJobCount > 0 && (effectiveState == AutoDispatchState.Ready || (printer.DispatchState?.BedPreConfirmed ?? false)))
        {
            return "Bed is clear. The next queued job will start automatically.";
        }

        return null;
    }

    private async Task<Dictionary<Guid, OccupyingJobSelection>> GetCurrentJobsByPrinterAsync(
        IEnumerable<Guid> printerIds,
        CancellationToken ct)
    {
        List<Guid> ids = printerIds.ToList();
        List<OccupyingJobSelection> currentJobs = await db.PrintJobs
            .WhereOccupiesPrinter()
            .Where(j => j.AssignedPrinterId.HasValue && ids.Contains(j.AssignedPrinterId.Value))
            .Select(j => new OccupyingJobSelection(
                j.AssignedPrinterId!.Value,
                j.Name ?? j.GcodeFile!.Name,
                j.Status,
                j.ActualStartTime ?? j.QueuedAt))
            .ToListAsync(ct);

        return currentJobs
            .GroupBy(job => job.PrinterId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(job => job.SortTime).First());
    }

    private async Task<QueuedJobSelection> GetQueuedJobSelectionAsync(Guid printerId, bool includeGcodeFile, CancellationToken ct)
    {
        UnassignedJobScoringContext scoringContext = await BuildUnassignedJobScoringContextAsync(includeGcodeFile, ct);
        return await GetQueuedJobSelectionAsync(printerId, includeGcodeFile, scoringContext, ct);
    }

    /// <summary>
    /// Builds a printer's queued-job selection using a scoring context shared across every
    /// printer in the request (see <see cref="UnassignedJobScoringContext"/>). Only the
    /// printer-scoped assigned-job query and DispatchSettings/scorer lookups.
    /// </summary>
    private async Task<QueuedJobSelection> GetQueuedJobSelectionAsync(
        Guid printerId,
        bool includeGcodeFile,
        UnassignedJobScoringContext scoringContext,
        CancellationToken ct)
    {
        // Ready-head selection MUST use the single shared ordering selector so the job the
        // operator sees at the head of the queue is exactly the job that gets dispatched.
        IQueryable<PrintJob> assignedQuery = db.PrintJobs
            .AsNoTracking()
            .Where(j => j.AssignedPrinterId == printerId && j.Status == PrintJobStatus.Queued)
            .OrderByPriorityDescending();

        if (includeGcodeFile)
        {
            assignedQuery = assignedQuery.Include(j => j.GcodeFile);
        }

        List<PrintJob> eligibleJobs = await assignedQuery.ToListAsync(ct);

        if (dispatchScorer is null)
        {
            return new QueuedJobSelection(
                eligibleJobs.OrderByPriorityDescending().FirstOrDefault(),
                eligibleJobs.Count);
        }

        foreach (PrintJob job in scoringContext.UnassignedJobs)
        {
            if (!scoringContext.ScoresByJobId.TryGetValue(job.Id, out Dictionary<Guid, DispatchScore>? printerScores))
            {
                continue;
            }

            printerScores.TryGetValue(printerId, out DispatchScore? printerScore);

            if (printerScore is null || printerScore.Eliminated || printerScore.TotalScore < scoringContext.MinimumScoreThreshold)
            {
                continue;
            }

            eligibleJobs.Add(job);
        }

        return new QueuedJobSelection(
            eligibleJobs.OrderByPriorityDescending().FirstOrDefault(),
            eligibleJobs.Count);
    }

    /// <summary>
    /// Fetches the unassigned-queued-job pool once and scores each job exactly once against
    /// every enabled printer (<see cref="IDispatchScorer.ScorePrintersForJobAsync"/> already
    /// returns per-printer scores in a single call), and reads <see cref="DispatchSettings"/>
    /// once. The resulting context is reused for every printer's eligibility check instead of
    /// re-querying and re-scoring per printer.
    /// </summary>
    private async Task<UnassignedJobScoringContext> BuildUnassignedJobScoringContextAsync(
        bool includeGcodeFile,
        CancellationToken ct)
    {
        IQueryable<PrintJob> unassignedQuery = db.PrintJobs
            .AsNoTracking()
            .Where(j => j.AssignedPrinterId == null && j.Status == PrintJobStatus.Queued)
            .OrderByPriorityDescending();

        if (includeGcodeFile)
        {
            unassignedQuery = unassignedQuery.Include(j => j.GcodeFile);
        }

        List<PrintJob> unassignedJobs = await unassignedQuery.ToListAsync(ct);

        if (dispatchScorer is null)
        {
            return new UnassignedJobScoringContext(unassignedJobs, [], MinimumScoreThreshold: 0);
        }

        DispatchSettings? settings = await db.DispatchSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        double minimumScoreThreshold = settings?.MinimumScoreThreshold ?? 0;

        Dictionary<Guid, Dictionary<Guid, DispatchScore>> scoresByJobId = [];
        foreach (PrintJob job in unassignedJobs)
        {
            List<DispatchScore> scores = await dispatchScorer.ScorePrintersForJobAsync(job.Id, ct);
            scoresByJobId[job.Id] = scores.ToDictionary(score => score.PrinterId);
        }

        return new UnassignedJobScoringContext(unassignedJobs, scoresByJobId, minimumScoreThreshold);
    }

    private async Task<FilamentCheckResult> CheckFilamentAsync(Printer printer, PrintJob nextJob, CancellationToken ct)
        => await FilamentPreflightEvaluator.CheckAsync(
            printer,
            nextJob,
            spoolmanService,
            logger,
            ct);
}
