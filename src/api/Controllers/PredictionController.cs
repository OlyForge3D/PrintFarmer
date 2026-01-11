using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for job completion time predictions (Phase 4.2)
/// Provides endpoints for getting predicted completion times and statistics
/// </summary>
[ApiController]
[Route("api/predictions")]
[Produces("application/json")]
public class PredictionController(PredictionService predictionService) : ControllerBase
{
    /// <summary>
    /// Get predicted completion time for a job
    /// </summary>
    /// <param name="jobId">The job ID to predict for</param>
    /// <param name="job">The print job entity (injected via resolver)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Prediction with estimated completion time and confidence level</returns>
    [HttpGet("jobs/{jobId:guid}/completion")]
    [ProducesResponseType(typeof(CompletionPredictionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CompletionPredictionDto>> GetCompletionPredictionAsync(
        Guid jobId,
        [FromServices] AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var job = await dbContext.PrintJobs.FindAsync(new object[] { jobId }, cancellationToken: cancellationToken);
            if (job == null)
            {
                return NotFound($"Job {jobId} not found");
            }

            var prediction = await predictionService.PredictCompletionTimeAsync(jobId, job, cancellationToken);
            return Ok(prediction);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get recorded statistics for a completed job
    /// </summary>
    /// <param name="jobId">The job ID to get statistics for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Job statistics if available</returns>
    [HttpGet("jobs/{jobId:guid}/statistics")]
    [ProducesResponseType(typeof(PrintJobStatisticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrintJobStatisticsDto>> GetJobStatisticsAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            var stats = await predictionService.GetJobStatisticsAsync(jobId, cancellationToken);
            if (stats == null)
            {
                return NotFound($"Statistics for job {jobId} not found");
            }

            return Ok(stats);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get duration statistics by material type
    /// </summary>
    /// <param name="material">Material type to filter (optional)</param>
    /// <param name="printerId">Printer ID to filter (optional)</param>
    /// <param name="minSampleSize">Minimum number of samples required</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of material statistics</returns>
    [HttpGet("stats/by-material")]
    [ProducesResponseType(typeof(Dictionary<string, DurationStatsDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<Dictionary<string, DurationStatsDto>>> GetMaterialStatsAsync(
        [FromQuery] string? material = null,
        [FromQuery] Guid? printerId = null,
        [FromQuery] int minSampleSize = 3,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = await predictionService.GetMaterialStatsAsync(printerId, cancellationToken);
            return Ok(stats);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get duration statistics for a specific printer model
    /// </summary>
    /// <param name="modelId">Printer model ID to filter</param>
    /// <param name="material">Material type to filter (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Duration statistics for the model</returns>
    [HttpGet("stats/model/{modelId:guid}")]
    [ProducesResponseType(typeof(DurationStatsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DurationStatsDto>> GetModelStatsAsync(
        Guid modelId,
        [FromQuery] string? material = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = await predictionService.GetDurationStatsAsync(
                modelId: modelId,
                material: material,
                minSampleSize: 3,
                cancellationToken: cancellationToken);

            if (stats == null)
            {
                return NotFound($"Insufficient data for model {modelId}");
            }

            return Ok(stats);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Record a job completion for learning (admin only)
    /// </summary>
    /// <param name="request">Completion record request</param>
    /// <param name="dbContext">Database context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success response</returns>
    [HttpPost("jobs/{jobId:guid}/record-completion")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RecordCompletionAsync(
        Guid jobId,
        [FromBody] RecordCompletionRequest request,
        [FromServices] AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request.ActualDurationMs <= 0)
            {
                return BadRequest("ActualDurationMs must be greater than 0");
            }

            var job = await dbContext.PrintJobs.FindAsync(new object[] { jobId }, cancellationToken: cancellationToken);
            if (job == null)
            {
                return NotFound($"Job {jobId} not found");
            }

            await predictionService.RecordJobCompletionAsync(
                job,
                request.ActualDurationMs,
                request.IsSuccess,
                request.FailureReason,
                cancellationToken);

            return Ok(new { message = "Completion recorded successfully" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

/// <summary>
/// Request model for recording job completion
/// </summary>
public class RecordCompletionRequest
{
    /// <summary>Actual duration of the print in milliseconds</summary>
    public long ActualDurationMs { get; set; }

    /// <summary>Whether the job completed successfully</summary>
    public bool IsSuccess { get; set; } = true;

    /// <summary>Reason for failure if not successful</summary>
    public string? FailureReason { get; set; }
}
