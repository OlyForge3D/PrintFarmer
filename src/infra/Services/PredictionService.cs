using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;

namespace Farm.Infrastructure.Services;

/// <summary>
/// Service for predicting job completion times based on historical data (Phase 4.2)
/// Analyzes past job durations to estimate future job completion with confidence levels
/// </summary>
public class PredictionService(IPrintJobStatisticsRepository repository, IQueueRepository queueRepository)
{
    /// <summary>
    /// Calculates the confidence level based on sample size
    /// </summary>
    /// <remarks>
    /// - High: ±10% accuracy with 10+ samples
    /// - Medium: ±20% accuracy with 3-9 samples
    /// - Low: ±50% accuracy with 1-2 samples
    /// </remarks>
    private static ConfidenceLevel CalculateConfidenceLevel(int sampleSize) =>
        sampleSize switch
        {
            >= 10 => ConfidenceLevel.High,
            >= 3 => ConfidenceLevel.Medium,
            _ => ConfidenceLevel.Low
        };

    /// <summary>
    /// Gets the variance percentage for a confidence level
    /// </summary>
    private static double GetVariancePercent(ConfidenceLevel confidence) =>
        confidence switch
        {
            ConfidenceLevel.High => 10,
            ConfidenceLevel.Medium => 20,
            _ => 50
        };

    /// <summary>
    /// Predicts the completion time for a print job based on similar historical jobs
    /// </summary>
    /// <param name="jobId">The job ID to predict for</param>
    /// <param name="job">The print job entity (required to get model and material info)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Prediction DTO with estimated completion time and confidence level</returns>
    public async Task<CompletionPredictionDto> PredictCompletionTimeAsync(
        Guid jobId,
        PrintJob job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Get printer model from GcodeFile metadata or assigned printer
        var modelId = GetPrinterModelId(job);
        var material = job.RequiredMaterialType;

        // Query similar historical jobs
        var similarJobs = await repository.GetByModelAndMaterialAsync(
            modelId,
            material,
            successfulOnly: true,
            limit: 100,
            cancellationToken);

        // If no historical data, return estimate based on gcode estimate
        if (similarJobs.Count == 0)
        {
            return new CompletionPredictionDto
            {
                JobId = jobId.ToString(),
                EstimatedCompletionTime = CalculateEstimatedCompletion(job),
                EstimatedDuration = job.EstimatedPrintTime,
                Confidence = ConfidenceLevel.Low,
                SampleSize = 0,
                VariancePercent = 50.0,
                Note = "No historical data available. Using gcode estimate."
            };
        }

        // Calculate statistics from similar jobs
        var avgDurationMs = similarJobs
            .Where(s => s.ActualDurationMs.HasValue)
            .Average(s => s.ActualDurationMs!.Value);

        var estimatedDuration = TimeSpan.FromMilliseconds(avgDurationMs);
        var variance = CalculateVariance(similarJobs, avgDurationMs);
        var confidence = CalculateConfidenceLevel(similarJobs.Count);
        var variancePercent = GetVariancePercent(confidence);

        return new CompletionPredictionDto
        {
            JobId = jobId.ToString(),
            EstimatedCompletionTime = DateTime.UtcNow.Add(estimatedDuration),
            EstimatedDuration = estimatedDuration,
            Confidence = confidence,
            SampleSize = similarJobs.Count,
            VariancePercent = variancePercent,
            Note = GeneratePredictionNote(confidence, similarJobs.Count, material, modelId)
        };
    }

    /// <summary>
    /// Predicts the completion time for a print job by job ID (controller-friendly overload)
    /// Looks up the job entity internally before prediction
    /// </summary>
    /// <param name="jobId">The job ID to predict for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Prediction DTO with estimated completion time and confidence level</returns>
    /// <exception cref="InvalidOperationException">Thrown when job is not found</exception>
    public async Task<CompletionPredictionDto> PredictCompletionTimeByJobIdAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await queueRepository.FindByIdAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job {jobId} not found");

        return await PredictCompletionTimeAsync(jobId, job, cancellationToken);
    }

    /// <summary>
    /// Records actual job completion for learning (controller-friendly overload)
    /// Looks up the job entity internally before recording
    /// </summary>
    /// <param name="jobId">The job ID to record completion for</param>
    /// <param name="actualDurationMs">Actual duration in milliseconds</param>
    /// <param name="isSuccess">Whether the job completed successfully</param>
    /// <param name="failureReason">Reason for failure if not successful</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Completion task</returns>
    /// <exception cref="InvalidOperationException">Thrown when job is not found</exception>
    public async Task RecordCompletionByJobIdAsync(
        Guid jobId,
        long actualDurationMs,
        bool isSuccess,
        string? failureReason = null,
        CancellationToken cancellationToken = default)
    {
        var job = await queueRepository.FindByIdAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job {jobId} not found");

        await RecordJobCompletionAsync(job, actualDurationMs, isSuccess, failureReason, cancellationToken);
    }

    /// <summary>
    /// Records actual job completion for learning
    /// </summary>
    public async Task RecordJobCompletionAsync(
        PrintJob job,
        long actualDurationMs,
        bool isSuccess,
        string? failureReason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var statistics = new PrintJobStatistics
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            ActualDurationMs = actualDurationMs,
            EstimatedDurationMs = job.EstimatedPrintTime != null ? (long)job.EstimatedPrintTime.Value.TotalMilliseconds : null,
            PrinterModelId = GetPrinterModelId(job),
            Material = job.RequiredMaterialType,
            IsSuccess = isSuccess,
            FailureReason = failureReason,
            CompletedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await repository.AddAsync(statistics, cancellationToken);
    }

    /// <summary>
    /// Gets duration statistics for a printer model and material combination
    /// </summary>
    public async Task<PredictionDurationStatsDto?> GetDurationStatsAsync(
        Guid? modelId = null,
        string? material = null,
        DateTime? fromDate = null,
        int? minSampleSize = 3,
        CancellationToken cancellationToken = default)
    {
        List<PrintJobStatistics> stats;

        if (modelId.HasValue && !string.IsNullOrWhiteSpace(material))
        {
            stats = await repository.GetByModelAndMaterialAsync(
                modelId,
                material,
                successfulOnly: true,
                limit: 1000,
                cancellationToken);
        }
        else if (modelId.HasValue)
        {
            stats = await repository.GetByPrinterModelAsync(
                modelId.Value,
                successfulOnly: true,
                fromDate: fromDate,
                cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(material))
        {
            stats = await repository.GetByMaterialAsync(
                material,
                successfulOnly: true,
                fromDate: fromDate,
                cancellationToken);
        }
        else
        {
            stats = await repository.GetSuccessfulJobsAsync(
                fromDate: fromDate,
                cancellationToken: cancellationToken);
        }

        // Return null if insufficient data
        if (stats.Count < (minSampleSize ?? 3))
        {
            return null;
        }

        // Calculate statistics from similar jobs
        var validDurations = stats
            .Where(s => s.ActualDurationMs.HasValue)
            .Select(s => s.ActualDurationMs!.Value)
            .OrderBy(d => d)
            .ToList();

        if (validDurations.Count == 0)
        {
            return null;
        }

        var avg = validDurations.Average();
        var variance = CalculateVariance(validDurations, avg);
        var median = validDurations.Count % 2 == 0
            ? (validDurations[validDurations.Count / 2 - 1] + validDurations[validDurations.Count / 2]) / 2
            : validDurations[validDurations.Count / 2];

        var totalCount = stats.Count;
        var successCount = stats.Count(s => s.IsSuccess);

        return new PredictionDurationStatsDto
        {
            TotalJobs = totalCount,
            SuccessfulJobs = successCount,
            SuccessRate = totalCount > 0 ? (double)successCount / totalCount : 0,
            AverageDuration = TimeSpan.FromMilliseconds(avg),
            MedianDuration = TimeSpan.FromMilliseconds(median),
            MinDuration = TimeSpan.FromMilliseconds(validDurations[0]),
            MaxDuration = TimeSpan.FromMilliseconds(validDurations[validDurations.Count - 1]),
            StandardDeviation = variance,
            Variance = variance * variance,
            Material = material,
            PrinterModelName = null // Would need printer model lookup
        };
    }

    /// <summary>
    /// Gets recorded statistics for a specific job
    /// </summary>
    public async Task<PrintJobStatisticsDto?> GetJobStatisticsAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var stats = await repository.GetByJobIdAsync(jobId, cancellationToken);
        return stats == null
            ? null
            : new PrintJobStatisticsDto
            {
                JobId = jobId.ToString(),
                ActualDurationMs = stats.ActualDurationMs,
                EstimatedDurationMs = stats.EstimatedDurationMs,
                Material = stats.Material,
                NozzleTemperature = stats.NozzleTemperature,
                BedTemperature = stats.BedTemperature,
                SpeedPercentage = stats.SpeedPercentage,
                IsSuccess = stats.IsSuccess,
                FailureReason = stats.FailureReason,
                CompletedAtUtc = stats.CompletedAtUtc
            };
    }

    /// <summary>
    /// Gets material statistics across all printers
    /// </summary>
    public async Task<Dictionary<string, PredictionDurationStatsDto>> GetMaterialStatsAsync(
        Guid? printerId = null,
        CancellationToken cancellationToken = default)
    {
        var stats = await repository.GetSuccessfulJobsAsync(cancellationToken: cancellationToken);

        if (printerId.HasValue)
        {
            // TODO: Filter by printer ID if needed
            // This would require joining with PrintJob entity
        }

        var groupedByMaterial = stats
            .Where(s => !string.IsNullOrWhiteSpace(s.Material))
            .GroupBy(s => s.Material!)
            .ToList();

        var result = new Dictionary<string, PredictionDurationStatsDto>();

        foreach (var group in groupedByMaterial)
        {
            var validDurations = group
                .Where(s => s.ActualDurationMs.HasValue)
                .Select(s => s.ActualDurationMs!.Value)
                .OrderBy(d => d)
                .ToList();

            if (validDurations.Count == 0)
            {
                continue;
            }

            var avg = validDurations.Average();
            var median = validDurations.Count % 2 == 0
                ? (validDurations[validDurations.Count / 2 - 1] + validDurations[validDurations.Count / 2]) / 2
                : validDurations[validDurations.Count / 2];

            var variance = CalculateVariance(validDurations, avg);

            result[group.Key] = new PredictionDurationStatsDto
            {
                TotalJobs = group.Count(),
                SuccessfulJobs = group.Count(s => s.IsSuccess),
                SuccessRate = (double)group.Count(s => s.IsSuccess) / group.Count(),
                AverageDuration = TimeSpan.FromMilliseconds(avg),
                MedianDuration = TimeSpan.FromMilliseconds(median),
                MinDuration = TimeSpan.FromMilliseconds(validDurations[0]),
                MaxDuration = TimeSpan.FromMilliseconds(validDurations[validDurations.Count - 1]),
                StandardDeviation = variance,
                Variance = variance * variance,
                Material = group.Key
            };
        }

        return result;
    }

    /// <summary>
    /// Helper: Calculate average variance from jobs
    /// </summary>
    private static double CalculateVariance(List<PrintJobStatistics> stats, double average)
    {
        if (stats.Count <= 1)
        {
            return 0;
        }

        var variance = stats
            .Where(s => s.ActualDurationMs.HasValue)
            .Average(s => Math.Pow(s.ActualDurationMs!.Value - average, 2));

        return Math.Sqrt(variance);
    }

    /// <summary>
    /// Helper: Calculate variance from list of durations
    /// </summary>
    private static double CalculateVariance(List<long> durations, double average)
    {
        if (durations.Count <= 1)
        {
            return 0;
        }

        var variance = durations.Average(d => Math.Pow(d - average, 2));
        return Math.Sqrt(variance);
    }

    /// <summary>
    /// Helper: Get printer model ID from job
    /// </summary>
#pragma warning disable S1172 // Parameter is reserved for future implementation
    private static Guid? GetPrinterModelId(PrintJob job)
    {
        // TODO: Implement logic to get printer model from assigned printer or gcode file metadata
        // For now, return null - job parameter reserved for future implementation
        return null;
    }
#pragma warning restore S1172

    /// <summary>
    /// Helper: Calculate estimated completion time
    /// </summary>
    private static DateTime CalculateEstimatedCompletion(PrintJob job)
    {
        if (job.EstimatedPrintTime.HasValue)
        {
            return DateTime.UtcNow.Add(job.EstimatedPrintTime.Value);
        }

        // Default to 2 hours if unknown
        return DateTime.UtcNow.AddHours(2);
    }

    /// <summary>
    /// Helper: Generate human-friendly prediction note
    /// </summary>
#pragma warning disable S1172 // Parameter is reserved for future implementation
    private static string GeneratePredictionNote(ConfidenceLevel confidence, int sampleSize, string? material, Guid? modelId)
    {
        var materialStr = !string.IsNullOrWhiteSpace(material) ? $" {material}" : "";
        return confidence switch
        {
            ConfidenceLevel.High => $"Based on {sampleSize}{materialStr} jobs with high confidence.",
            ConfidenceLevel.Medium => $"Based on {sampleSize}{materialStr} jobs with medium confidence. Check recent conditions.",
            _ => $"Based on {sampleSize}{materialStr} job(s). Limited historical data - actual time may vary."
        };
    }
#pragma warning restore S1172
}

/// <summary>
/// Confidence level for completion time prediction (Phase 4.2)
/// </summary>
public enum ConfidenceLevel
{
    /// <summary>±10% accuracy with 10+ samples</summary>
    High,
    /// <summary>±20% accuracy with 3-9 samples</summary>
    Medium,
    /// <summary>±50% accuracy with 1-2 samples</summary>
    Low
}

/// <summary>
/// DTO for completion time prediction
/// </summary>
public class CompletionPredictionDto
{
    public string JobId { get; set; } = string.Empty;

    public DateTime EstimatedCompletionTime { get; set; }

    public TimeSpan? EstimatedDuration { get; set; }

    public ConfidenceLevel Confidence { get; set; }

    public int SampleSize { get; set; }

    public double? VariancePercent { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// DTO for duration statistics from prediction service
/// </summary>
public class PredictionDurationStatsDto
{
    public int TotalJobs { get; set; }

    public int SuccessfulJobs { get; set; }

    public double SuccessRate { get; set; }           // 0.0 to 1.0

    public TimeSpan AverageDuration { get; set; }

    public TimeSpan MedianDuration { get; set; }

    public TimeSpan MinDuration { get; set; }

    public TimeSpan MaxDuration { get; set; }

    public double StandardDeviation { get; set; }

    public double Variance { get; set; }

    public string? Material { get; set; }

    public string? PrinterModelName { get; set; }
}

/// <summary>
/// DTO for recorded job statistics
/// </summary>
public class PrintJobStatisticsDto
{
    public string JobId { get; set; } = string.Empty;

    public long? ActualDurationMs { get; set; }

    public long? EstimatedDurationMs { get; set; }

    public string? Material { get; set; }

    public int? NozzleTemperature { get; set; }

    public int? BedTemperature { get; set; }

    public int SpeedPercentage { get; set; }

    public bool IsSuccess { get; set; }

    public string? FailureReason { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}
