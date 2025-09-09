using System.Text.Json;
using Farm.Web.Shared;
using StackExchange.Redis;
using Farm.OrcaSlicer.Worker.Health;

namespace Farm.OrcaSlicer.Worker.Services;

public class QueueConsumerService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ISlicingPipelineService _pipeline;
    private readonly IProgressReporter _progressReporter;
    private readonly ILogger<QueueConsumerService> _logger;
    private readonly string _queueKey;
    private readonly string _processingKey;
    private readonly string _workerId;
    private readonly IWorkerStateService _workerStateService;

    public QueueConsumerService(
        IConnectionMultiplexer redis,
        ISlicingPipelineService pipeline,
        IProgressReporter progressReporter,
        ILogger<QueueConsumerService> logger,
        IConfiguration configuration,
        IWorkerStateService workerStateService)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _progressReporter = progressReporter ?? throw new ArgumentNullException(nameof(progressReporter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerStateService = workerStateService ?? throw new ArgumentNullException(nameof(workerStateService));
        ArgumentNullException.ThrowIfNull(configuration);
        _queueKey = configuration["Worker:QueueKey"] ?? "slicer:queue:orcaslicer";
        _processingKey = configuration["Worker:ProcessingKey"] ?? "slicer:processing";
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
                var result = await database.ListRightPopLeftPushAsync(_queueKey, _processingKey);
                if (!result.HasValue)
                {
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
                job.WorkerId = _workerId;
                job.StartedAt = DateTime.UtcNow;
                job.Status = SlicingJobStatus.Slicing;
                _workerStateService.IncrementActiveJobs();
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
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        _logger.LogInformation("OrcaSlicer worker {WorkerId} stopped", _workerId);
    }

    private async Task ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var startTime = DateTime.UtcNow;
        _logger.LogInformation("Starting processing job {JobId} at {StartTime}", job.Id, startTime);
        try
        {
            await _progressReporter.ReportProgressAsync(job.Id, 0, "Starting slicing process", cancellationToken);
            var result = await _pipeline.ProcessJobAsync(job, cancellationToken);
            job.Status = SlicingJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            job.ResultFileUrl = result.ResultFileUrl;
            job.EstimatedPrintTimeSeconds = result.EstimatedPrintTimeSeconds;
            job.EstimatedFilamentUsageGrams = result.EstimatedFilamentUsageGrams;
            job.OutputFileSizeBytes = result.OutputFileSizeBytes;
            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation("Completed job {JobId} in {Duration}ms", job.Id, duration.TotalMilliseconds);
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
            var database = _redis.GetDatabase();
            await database.ListRemoveAsync(_processingKey, JsonSerializer.Serialize(job));
            _workerStateService.DecrementActiveJobs();
        }
    }
}
