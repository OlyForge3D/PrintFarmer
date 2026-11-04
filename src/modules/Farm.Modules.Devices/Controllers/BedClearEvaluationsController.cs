using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Modules.Devices.Controllers;

/// <summary>Reviews shadow bed-clear evidence and reports independently labeled metrics.</summary>
[ApiController]
[Route("api/bed-clear-detector")]
[Tags("Bed-Clear Detector")]
[Authorize]
public sealed class BedClearEvaluationsController(
    AppDbContext db,
    IStoragePathService storagePathService,
    IQueueResourceAuthorizationService queueResourceAuthorization) : ControllerBase
{
    /// <summary>Requests a shadow-only sample without acknowledging readiness or moving the printer.</summary>
    [HttpPost("printers/{printerId:guid}/evaluations")]
    [RequirePermission(PrintFarmerPermissions.Queue.AcknowledgeBedClear)]
    public async Task<IActionResult> CaptureAsync(Guid printerId, CancellationToken ct)
    {
        if (!await CanAccessPrinterAsync(printerId, PrinterGroupAccessLevel.Submit, ct))
        {
            return NotFound();
        }

        Printer? printer = await db.Printers.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == printerId, ct);
        if (printer is null)
        {
            return NotFound();
        }

        DateTime now = DateTime.UtcNow;
        var sample = new BedClearEvaluation
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            BedTypeId = printer.BedTypeId,
            AcknowledgedBySubject = QueueActorIdentity.Resolve(User),
            AcknowledgedAtUtc = now,
            CreatedAtUtc = now,
        };
        db.BedClearEvaluations.Add(sample);
        await db.SaveChangesAsync(ct);
        return Accepted(new { sample.Id });
    }

    [HttpGet("printers/{printerId:guid}/evaluations")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    public async Task<IActionResult> GetEvaluationsAsync(Guid printerId, CancellationToken ct)
    {
        if (!await CanAccessPrinterAsync(printerId, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

        var evaluations = await db.BedClearEvaluations
            .AsNoTracking()
            .Where(evaluation => evaluation.PrinterId == printerId)
            .OrderByDescending(evaluation => evaluation.AcknowledgedAtUtc)
            .Take(100)
            .Select(evaluation => new
            {
                evaluation.Id,
                evaluation.PrinterId,
                evaluation.CameraId,
                evaluation.PrintJobId,
                evaluation.BedTypeId,
                evaluation.SourceAcknowledgementId,
                evaluation.AcknowledgedBySubject,
                evaluation.AcknowledgedAtUtc,
                evaluation.CapturedAtUtc,
                evaluation.IsReferenceEligible,
                evaluation.ToolheadX,
                evaluation.ToolheadY,
                evaluation.ToolheadZ,
                state = evaluation.State.ToString(),
                verdict = evaluation.Verdict.ToString(),
                evaluation.Score,
                evaluation.ReasonCode,
                groundTruth = evaluation.GroundTruth.HasValue
                    ? evaluation.GroundTruth.Value.ToString()
                    : null,
                evaluation.GroundTruthBySubject,
                evaluation.GroundTruthAtUtc,
                evaluation.IsActiveReference,
                hasImage = evaluation.ImageFilePath != null,
            })
            .ToListAsync(ct);

        return Ok(evaluations);
    }

    [HttpGet("printers/{printerId:guid}/metrics")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    public async Task<IActionResult> GetMetricsAsync(Guid printerId, CancellationToken ct)
    {
        if (!await CanAccessPrinterAsync(printerId, PrinterGroupAccessLevel.View, ct))
        {
            return NotFound();
        }

        var counts = await db.BedClearEvaluations
            .AsNoTracking()
            .Where(evaluation =>
                evaluation.PrinterId == printerId &&
                evaluation.State == BedClearEvaluationState.Complete &&
                evaluation.ImageFilePath != null &&
                evaluation.GroundTruth != null &&
                evaluation.GroundTruthBySubject != null &&
                evaluation.GroundTruthBySubject != evaluation.AcknowledgedBySubject)
            .GroupBy(evaluation => evaluation.PrinterId)
            .Select(group => new
            {
                Labeled = group.Count(),
                Matching = group.Count(evaluation =>
                    (evaluation.Verdict == BedClearDetectorVerdict.Clear &&
                     evaluation.GroundTruth == BedClearGroundTruth.Clear) ||
                    (evaluation.Verdict == BedClearDetectorVerdict.Occupied &&
                     evaluation.GroundTruth == BedClearGroundTruth.Occupied)),
                Occupied = group.Count(evaluation =>
                    evaluation.GroundTruth == BedClearGroundTruth.Occupied),
                FalseClear = group.Count(evaluation =>
                    evaluation.GroundTruth == BedClearGroundTruth.Occupied &&
                    evaluation.Verdict == BedClearDetectorVerdict.Clear),
            })
            .SingleOrDefaultAsync(ct);

        int labeledCount = counts?.Labeled ?? 0;
        int matchingCount = counts?.Matching ?? 0;
        int occupiedCount = counts?.Occupied ?? 0;
        int falseClearCount = counts?.FalseClear ?? 0;

        return Ok(new
        {
            sampleCount = await db.BedClearEvaluations.CountAsync(
                evaluation => evaluation.PrinterId == printerId,
                ct),
            independentlyLabeledCount = labeledCount,
            agreementRate = labeledCount == 0 ? (double?)null : (double)matchingCount / labeledCount,
            independentlyLabeledOccupiedCount = occupiedCount,
            falseClearCount,
            falseClearRate = occupiedCount == 0 ? (double?)null : (double)falseClearCount / occupiedCount,
        });
    }

    [HttpGet("evaluations/{evaluationId:guid}/image")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    public async Task<IActionResult> GetImageAsync(Guid evaluationId, CancellationToken ct)
    {
        BedClearEvaluation? evaluation = await db.BedClearEvaluations
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == evaluationId, ct);
        if (evaluation?.ImageFilePath is null ||
            !await CanAccessPrinterAsync(
                evaluation.PrinterId,
                PrinterGroupAccessLevel.View,
                ct))
        {
            return NotFound();
        }

#pragma warning disable CA3003 // Guid DB lookup; path is canonicalized and containment-checked by TryResolveImagePath.
        if (!TryResolveImagePath(evaluation.ImageFilePath, out string fullPath) ||
            !System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }

        return PhysicalFile(fullPath, "image/jpeg");
#pragma warning restore CA3003
    }

    [HttpPut("evaluations/{evaluationId:guid}/label")]
    [RequirePermission(PrintFarmerPermissions.Queue.AcknowledgeBedClear)]
    public async Task<IActionResult> LabelAsync(
        Guid evaluationId,
        [FromBody] BedClearEvaluationLabelRequest request,
        CancellationToken ct)
    {
        if (!Enum.IsDefined(request.GroundTruth))
        {
            return BadRequest(new { error = "invalid_ground_truth" });
        }

        BedClearEvaluation? evaluation = await db.BedClearEvaluations
            .FirstOrDefaultAsync(candidate => candidate.Id == evaluationId, ct);
        if (evaluation is null ||
            !await CanAccessPrinterAsync(
                evaluation.PrinterId,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            return NotFound();
        }

        string actorSubject = QueueActorIdentity.Resolve(User);
        if (string.Equals(
                actorSubject,
                evaluation.AcknowledgedBySubject,
                StringComparison.Ordinal))
        {
            return Conflict(new
            {
                error = "independent_review_required",
                detail = "A different operator must label this acknowledgement sample.",
            });
        }

        if (evaluation.State != BedClearEvaluationState.Complete ||
            evaluation.ImageFilePath is null)
        {
            return Conflict(new
            {
                error = "sample_unavailable",
                detail = "Only completed samples with an image can be labeled.",
            });
        }

        evaluation.GroundTruth = request.GroundTruth;
        evaluation.GroundTruthBySubject = actorSubject;
        evaluation.GroundTruthAtUtc = DateTime.UtcNow;
        evaluation.IsActiveReference =
            request.GroundTruth == BedClearGroundTruth.Clear &&
            evaluation.IsReferenceEligible &&
            evaluation.CameraId.HasValue &&
            evaluation.BedTypeId.HasValue;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpDelete("evaluations/{evaluationId:guid}")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    public async Task<IActionResult> DeleteAsync(Guid evaluationId, CancellationToken ct)
    {
        BedClearEvaluation? evaluation = await db.BedClearEvaluations
            .FirstOrDefaultAsync(candidate => candidate.Id == evaluationId, ct);
        if (evaluation is null ||
            !await CanAccessPrinterAsync(
                evaluation.PrinterId,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            return NotFound();
        }

        if (evaluation.ImageFilePath is not null &&
            !TryResolveImagePath(evaluation.ImageFilePath, out _))
        {
            return BadRequest(new { error = "invalid_image_path" });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        int deleted = await db.BedClearEvaluations
            .Where(candidate =>
                candidate.Id == evaluationId &&
                candidate.State != BedClearEvaluationState.Processing)
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            return Conflict(new { error = "sample_processing", detail = "Retry after capture completes." });
        }

#pragma warning disable CA3003 // Guid DB lookup; path is canonicalized and containment-checked by TryResolveImagePath.
        if (evaluation.ImageFilePath is not null &&
            TryResolveImagePath(evaluation.ImageFilePath, out string deletePath) &&
            System.IO.File.Exists(deletePath))
        {
            System.IO.File.Delete(deletePath);
        }
#pragma warning restore CA3003

        await transaction.CommitAsync(ct);
        return NoContent();
    }

    private Task<bool> CanAccessPrinterAsync(
        Guid printerId,
        PrinterGroupAccessLevel accessLevel,
        CancellationToken ct) =>
        queueResourceAuthorization.CanAccessPrinterAsync(User, printerId, accessLevel, ct);

    private bool TryResolveImagePath(string relativePath, out string fullPath)
    {
        string root = storagePathService.GetSnapshotStorageDirectory();
        string canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string canonicalFull = Path.GetFullPath(Path.Join(root, relativePath));
        if (!canonicalFull.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            fullPath = string.Empty;
            return false;
        }

        fullPath = canonicalFull;
        return true;
    }
}

public sealed record BedClearEvaluationLabelRequest(BedClearGroundTruth GroundTruth);
