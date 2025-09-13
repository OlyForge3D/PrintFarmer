using System.Text.Json;
using Farm.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Farm.Slicer.Worker.Core;

public abstract class BaseQueueConsumerService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IProgressReporter _progress;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;
    private readonly IWorkerStateService _workerState;
    private readonly string _queueKey;
    private readonly string _processingKey;
    private readonly string _workerId = WorkerIdentity.Create();

    protected BaseQueueConsumerService(
        IConnectionMultiplexer redis,
        IProgressReporter progress,
        IServiceProvider serviceProvider,
        ILogger logger,
        IWorkerStateService workerState,
        string queueKey,
        string processingKey)
    {
        _redis = redis;
        _progress = progress;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _workerState = workerState;
        _queueKey = queueKey;
        _processingKey = processingKey;
    }

    protected abstract Task<SlicingResult> ExecutePipelineAsync(DistributedSlicingJob job, IServiceProvider scopeServices, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker {WorkerId} queue consumer starting. Queue={Queue} Processing={Processing}", _workerId, _queueKey, _processingKey);
        var db = _redis.GetDatabase();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var raw = await db.ListRightPopLeftPushAsync(_queueKey, _processingKey);
                if (!raw.HasValue)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }
                var json = raw.ToString();
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }
                DistributedSlicingJob? job = null;
                try
                {
                    job = JsonSerializer.Deserialize<DistributedSlicingJob>(json);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deserialize job payload: {Payload}", json);
                }
                if (job == null)
                {
                    continue;
                }
                job.WorkerId = _workerId;
                job.StartedAt = DateTime.UtcNow;
                job.Status = SlicingJobStatus.Slicing;
                _workerState.IncrementActiveJobs();
                await HandleJobAsync(job, db, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Queue consumer cancellation requested.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in queue loop; backing off");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        _logger.LogInformation("Worker {WorkerId} queue consumer stopped", _workerId);
    }

    private async Task HandleJobAsync(DistributedSlicingJob job, IDatabase db, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        try
        {
            await _progress.ReportProgressAsync(job.Id, 0, "Starting slicing", ct);
            using var scope = _serviceProvider.CreateScope();
            var result = await ExecutePipelineAsync(job, scope.ServiceProvider, ct);
            job.Status = SlicingJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            job.ResultFileUrl = result.ResultFileUrl;
            job.EstimatedPrintTimeSeconds = result.EstimatedPrintTimeSeconds;
            job.EstimatedFilamentUsageGrams = result.EstimatedFilamentUsageGrams;
            job.OutputFileSizeBytes = result.OutputFileSizeBytes;
            _logger.LogInformation("Job {JobId} completed in {Ms}ms", job.Id, (DateTime.UtcNow - start).TotalMilliseconds);
            await _progress.ReportCompletionAsync(job, result, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Job {JobId} cancelled", job.Id);
            job.Status = SlicingJobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            await _progress.ReportFailureAsync(job.Id, "Job cancelled", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed", job.Id);
            job.Status = SlicingJobStatus.Error;
            job.CompletedAt = DateTime.UtcNow;
            job.ErrorMessage = ex.Message;
            await _progress.ReportFailureAsync(job.Id, ex.Message, ct);
        }
        finally
        {
            // Remove from processing list by scanning for first matching job ID (JSON may differ after mutation)
            await RemoveProcessingEntryByIdAsync(db, job.Id);
            _workerState.DecrementActiveJobs();
        }
    }

    private async Task RemoveProcessingEntryByIdAsync(IDatabase db, Guid jobId)
    {
        // NOTE: Redis lists do not support direct remove by predicate; naive approach: read list, rebuild.
        var processingValues = await db.ListRangeAsync(_processingKey, 0, -1);
        if (processingValues.Length == 0)
        {
            return;
        }
        var retained = new List<RedisValue>(processingValues.Length);
        foreach (var val in processingValues)
        {
            try
            {
                var candidate = JsonSerializer.Deserialize<DistributedSlicingJob>(val!);
                if (candidate?.Id == jobId)
                {
                    continue; // drop
                }
            }
            catch { /* ignore parse errors; retain original */ }
            retained.Add(val);
        }
        // Replace list (transaction could be added if needed)
        var tran = _redis.GetDatabase().CreateTransaction();
        _ = tran.KeyDeleteAsync(_processingKey);
        foreach (var v in retained)
        {
            _ = tran.ListLeftPushAsync(_processingKey, v);
        }
        await tran.ExecuteAsync();
    }
}
