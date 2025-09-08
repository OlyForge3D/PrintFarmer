using System.Text.Json;
using Farm.Web.Shared;
using StackExchange.Redis;

namespace Farm.Slicer.Worker.Services;

/// <summary>
/// Background service that consumes slicing jobs from Redis queue
/// </summary>
public class QueueConsumerService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ISlicingPipelineService _pipeline;
    private readonly IProgressReporter _progressReporter;
    private readonly ILogger<QueueConsumerService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _queueKey;
    private readonly string _processingKey;
    private readonly string _workerId;

    public QueueConsumerService(
        IConnectionMultiplexer redis,
        ISlicingPipelineService pipeline,
        IProgressReporter progressReporter,
        ILogger<QueueConsumerService> logger,
        IConfiguration configuration)
    {
        _redis = redis;
        _pipeline = pipeline;
        _progressReporter = progressReporter;
        _logger = logger;
        _configuration = configuration;
        _queueKey = "slicer:queue:orcaslicer";
        _processingKey = "slicer:processing";
        _workerId = Environment.MachineName + "-" + Environment.ProcessId;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OrcaSlicer worker {WorkerId} starting queue consumer", _workerId);
        
        var database = _redis.GetDatabase();
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Try to dequeue a job from the Redis queue (blocking pop with timeout)
                var result = await database.ListRightPopLeftPushAsync(_queueKey, _processingKey);
                
                if (!result.HasValue)
                {
                    // No job available, wait a bit before checking again
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var jobJson = result.ToString();
                if (string.IsNullOrEmpty(jobJson))
                {
                    continue;
                }

                var job = JsonSerializer.Deserialize<DistributedSlicingJob>(jobJson);
                if (job == null)
                {
                    _logger.LogWarning("Failed to deserialize job from queue: {JobJson}", jobJson);
                    continue;
                }

                _logger.LogInformation("Processing job {JobId} for user {UserId}", job.Id, job.UserId);

                // Update job with worker info and start processing
                job.WorkerId = _workerId;
                job.StartedAt = DateTime.UtcNow;
                job.Status = SlicingJobStatus.Slicing;

                // Process the job through the pipeline
                await ProcessJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Queue consumer cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in queue consumer loop");
                // Wait before retrying to avoid tight error loops
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("OrcaSlicer worker {WorkerId} stopped", _workerId);
    }

    private async Task ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogInformation("Starting processing job {JobId} at {StartTime}", job.Id, startTime);

        try
        {
            // Report job started
            await _progressReporter.ReportProgressAsync(job.Id, 0, "Starting slicing process", cancellationToken);

            // Execute the slicing pipeline
            var result = await _pipeline.ProcessJobAsync(job, cancellationToken);

            // Update job with results
            job.Status = SlicingJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            job.ResultFileUrl = result.GcodeFileUrl;
            job.EstimatedPrintTimeSeconds = result.EstimatedPrintTimeSeconds;
            job.EstimatedFilamentUsageGrams = result.EstimatedFilamentUsageGrams;
            job.OutputFileSizeBytes = result.FileSizeBytes;

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation("Completed job {JobId} in {Duration}ms", job.Id, duration.TotalMilliseconds);

            // Report completion
            await _progressReporter.ReportCompletionAsync(job, result, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Job {JobId} was cancelled", job.Id);
            job.Status = SlicingJobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            await _progressReporter.ReportFailureAsync(job.Id, "Job was cancelled", cancellationToken);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogError(ex, "Failed to process job {JobId} after {Duration}ms", job.Id, duration.TotalMilliseconds);
            
            job.Status = SlicingJobStatus.Error;
            job.CompletedAt = DateTime.UtcNow;
            job.ErrorMessage = ex.Message;

            await _progressReporter.ReportFailureAsync(job.Id, ex.Message, cancellationToken);
        }
        finally
        {
            // Remove job from processing queue regardless of outcome
            var database = _redis.GetDatabase();
            await database.ListRemoveAsync(_processingKey, JsonSerializer.Serialize(job));
        }
    }
}