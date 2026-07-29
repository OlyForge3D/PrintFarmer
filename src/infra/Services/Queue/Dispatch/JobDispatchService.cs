using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Orchestrates dispatch operations: scoring candidates and assigning jobs to printers.
/// Delegates scoring to <see cref="IDispatchScorer"/> and job execution to
/// <see cref="IPrintJobManagementService"/>.
/// </summary>
public class JobDispatchService(
    IDispatchScorer scorer,
    IPrintJobManagementService printJobManagement,
    ISpoolmanService spoolmanService,
    AppDbContext db,
    ILogger<JobDispatchService> logger,
    IQueueResourceAuthorizationService? resourceAuthorization = null,
    IQueuePositionAllocator? positionAllocator = null) : IJobDispatchService
{
    public async Task<List<DispatchCandidateDto>> FindCandidatesAsync(Guid jobId, CancellationToken ct = default)
    {
        List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(jobId, ct);

        // Log candidates for audit trail
        foreach (DispatchScore score in scores.Where(s => !s.Eliminated))
        {
            db.DispatchLogs.Add(new DispatchLog
            {
                Id = Guid.NewGuid(),
                PrintJobId = jobId,
                PrinterId = score.PrinterId,
                Action = DispatchAction.Suggested,
                Score = score.TotalScore,
                ScoreBreakdown = JsonSerializer.Serialize(score.ScoreBreakdown),
                CreatedAtUtc = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);

        return scores.Select(s => new DispatchCandidateDto
        {
            PrinterId = s.PrinterId,
            PrinterName = s.PrinterName,
            Score = s.TotalScore,
            ScoreBreakdown = s.ScoreBreakdown.ToDictionary(
                kvp => kvp.Key,
                kvp => new FactorScoreDto
                {
                    FactorName = kvp.Value.FactorName,
                    Score = kvp.Value.Score,
                    Weight = kvp.Value.Weight,
                    WeightedScore = kvp.Value.WeightedScore,
                }),
            Eliminated = s.Eliminated,
            EliminationReasons = s.EliminationReasons,
        }).ToList();
    }

    public async Task<QueuedPrintJobDto> DispatchJobAsync(Guid jobId, Guid printerId, string userId, CancellationToken ct = default)
    {
        // No pre-computed score — score on demand
        List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(jobId, ct);
        DispatchScore? printerScore = scores.FirstOrDefault(s => s.PrinterId == printerId);

        return await DispatchJobCoreAsync(
            jobId,
            printerId,
            userId,
            printerScore,
            ifMatchJobRowVersion: null,
            ct);
    }

    public async Task<QueuedPrintJobDto> DispatchJobAsync(
        Guid jobId,
        Guid printerId,
        string userId,
        string? ifMatchJobRowVersion,
        CancellationToken ct = default)
    {
        List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(jobId, ct);
        DispatchScore? printerScore = scores.FirstOrDefault(s => s.PrinterId == printerId);
        return await DispatchJobCoreAsync(
            jobId,
            printerId,
            userId,
            printerScore,
            ifMatchJobRowVersion,
            ct);
    }

    public Task<QueuedPrintJobDto> DispatchJobAsync(Guid jobId, Guid printerId, string userId, DispatchScore preComputedScore, CancellationToken ct = default)
    {
        // Caller already scored — skip the redundant scoring pass
        return DispatchJobCoreAsync(
            jobId,
            printerId,
            userId,
            preComputedScore,
            ifMatchJobRowVersion: null,
            ct);
    }

    private async Task<QueuedPrintJobDto> DispatchJobCoreAsync(
        Guid jobId,
        Guid printerId,
        string userId,
        DispatchScore? printerScore,
        string? ifMatchJobRowVersion,
        CancellationToken ct)
    {
        PrintJob? job = await db.PrintJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new InvalidOperationException($"Print job {jobId} not found");

        Printer? printer = await db.Printers.FirstOrDefaultAsync(p => p.Id == printerId, ct)
            ?? throw new InvalidOperationException($"Printer {printerId} not found");

        if (resourceAuthorization is not null &&
            (!await resourceAuthorization.CanActorAccessJobAsync(
                 userId,
                 jobId,
                 PrinterGroupAccessLevel.Submit,
                 ct) ||
             !await resourceAuthorization.CanActorAccessPrinterAsync(
                 userId,
                 printerId,
                 PrinterGroupAccessLevel.Submit,
                 ct)))
        {
            throw new KeyNotFoundException($"Print job {jobId} not found.");
        }

        if (ifMatchJobRowVersion is not null)
        {
            try
            {
                QueueRevisionGuard.EnsureIfMatch(
                    ifMatchJobRowVersion,
                    job.RowVersion,
                    "scored job dispatch");
            }
            catch (QueueRevisionConflictException ex)
            {
                byte[]? dispatchStateRowVersion = await db.PrinterDispatchStates
                    .AsNoTracking()
                    .Where(state => state.PrinterId == printerId)
                    .Select(state => state.RowVersion)
                    .SingleOrDefaultAsync(ct);
                throw new QueueRevisionConflictException(
                    ex.Message,
                    job.RowVersion,
                    dispatchStateRowVersion);
            }
        }

        if (printerScore is { Eliminated: true })
        {
            string reasons = string.Join("; ", printerScore.EliminationReasons);
            throw new InvalidOperationException($"Printer '{printer.Name}' is eliminated: {reasons}");
        }

        if (job.JobKind == JobKind.FilamentCalibration &&
            job.AssignedPrinterId != printerId)
        {
            throw new QueueSemanticConflictException(
                "A calibration job's assigned printer is immutable.");
        }

        Guid? originalPrinterId = job.AssignedPrinterId;

        // Assign the job and log the dispatch in a single batch save
        if (originalPrinterId != printerId)
        {
            if (positionAllocator is not null)
            {
                job.QueuePosition = await positionAllocator.AllocateAsync(printerId, ct);
            }
            else if (db.Database.IsRelational())
            {
                throw new InvalidOperationException(
                    "A provider-native queue position allocator is required for printer reassignment.");
            }
            else
            {
                job.QueuePosition = await db.PrintJobs
                    .Where(candidate => candidate.AssignedPrinterId == printerId)
                    .Select(candidate => (int?)candidate.QueuePosition)
                    .MaxAsync(ct) ?? 0;
                job.QueuePosition++;
            }
        }

        job.AssignedPrinterId = printerId;
        job.DispatchedAt = DateTime.UtcNow;
        job.DispatchScore = printerScore?.TotalScore;
        job.DispatchMode = (int)DispatchMode.Suggested;

        // If the job doesn't have a SpoolmanFilamentId, inherit from the printer's active spool
        if (job.JobKind != JobKind.FilamentCalibration && printer.CurrentSpoolId.HasValue)
        {
            try
            {
                var spool = await spoolmanService.GetSpoolByIdAsync(printer.CurrentSpoolId.Value, ct);
                job.SpoolmanSpoolId = printer.CurrentSpoolId.Value;

                if (!job.SpoolmanFilamentId.HasValue && spool?.FilamentId != null)
                {
                    job.SpoolmanFilamentId = spool.FilamentId;
                    logger.LogInformation(
                        "Inherited SpoolmanFilamentId {FilamentId} from printer {PrinterName} spool {SpoolId}",
                        spool.FilamentId, printer.Name, printer.CurrentSpoolId.Value);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to look up spool {SpoolId} for filament inheritance", printer.CurrentSpoolId.Value);
            }
        }

        db.DispatchLogs.Add(new DispatchLog
        {
            Id = Guid.NewGuid(),
            PrintJobId = jobId,
            PrinterId = printerId,
            Action = DispatchAction.Dispatched,
            Score = printerScore?.TotalScore,
            ScoreBreakdown = printerScore is not null
                ? JsonSerializer.Serialize(printerScore.ScoreBreakdown)
                : null,
            Reason = $"Dispatched by {userId}",
            CreatedAtUtc = DateTime.UtcNow,
        });

        foreach (Guid affectedPrinterId in new[] { originalPrinterId, (Guid?)printerId }
                     .Where(value => value.HasValue)
                     .Select(value => value!.Value)
                     .Distinct())
        {
            PrinterDispatchState? state = await db.PrinterDispatchStates
                .FirstOrDefaultAsync(
                    candidate => candidate.PrinterId == affectedPrinterId,
                    ct);
            if (state is not null)
            {
                state.QueueRevision++;
                state.AcknowledgedJobId = null;
                state.AcknowledgedAtUtc = null;
                state.AcknowledgedBySubject = null;
                state.AcknowledgementIdempotencyKey = null;
                state.AcknowledgementExpiresAtUtc = null;
                state.AcknowledgedJobRowVersion = null;
                state.AcknowledgedQueueRevision = null;
                state.AcknowledgedPrinterConfigRevision = null;
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Dispatching job {JobId} to printer {PrinterName} (score: {Score})",
            jobId, printer.Name, printerScore?.TotalScore ?? 0);

        string postAssignmentEtag = Convert.ToBase64String(job.RowVersion ?? []);
        return await printJobManagement.DispatchJobAsync(
            jobId.ToString(),
            userId,
            postAssignmentEtag,
            ct);
    }
}
