using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Repositories.Slicing;
using Farm.Web.Api.Repositories.Workers;
using Farm.Web.Api.Services.Slicing;

namespace Farm.Web.Api.Services.JobDispatch;

/// <summary>
/// Service for dispatching jobs to available workers based on capabilities and load balancing
/// </summary>
public interface IJobDispatcherService
{
    /// <summary>
    /// Attempt to dispatch the next queued job to an available worker
    /// </summary>
    /// <returns>True if a job was dispatched, false if no suitable worker was found</returns>
    Task<bool> DispatchNextJobAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatch a specific job to the best available worker
    /// </summary>
    Task<bool> DispatchJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find the best worker for a job based on capabilities and load
    /// </summary>
    Task<Worker?> FindBestWorkerForJobAsync(SliceJob job, CancellationToken cancellationToken = default);
}

public class JobDispatcherService : IJobDispatcherService
{
    private readonly ISliceJobRepository _jobRepository;
    private readonly IWorkerRepository _workerRepository;
    private readonly ISliceJobEventService _eventService;
    private readonly IUnifiedLoggingService _logger;
    private readonly IHttpClientFactory _httpClientFactory;
        private readonly RetryOptions _retryOptions;
    
    private static volatile int _lastAvailableWorkers;
    private static readonly System.Diagnostics.Metrics.Meter _meter = new("PrintFarmer.Slicing", "1.0.0");
    private static readonly System.Diagnostics.Metrics.Counter<int> _jobsDispatched = _meter.CreateCounter<int>("slicing_jobs_dispatched");
    private static readonly System.Diagnostics.Metrics.Counter<int> _jobsDispatchFailed = _meter.CreateCounter<int>("slicing_jobs_dispatch_failed");
    private static readonly System.Diagnostics.Metrics.Histogram<double> _dispatchDurationMs = _meter.CreateHistogram<double>("slicing_job_dispatch_duration_ms", unit: "ms", description: "Duration of job dispatch attempts");
    private static readonly System.Diagnostics.Metrics.ObservableGauge<int> _availableWorkersGauge = _meter.CreateObservableGauge("slicing_available_workers", () => new System.Diagnostics.Metrics.Measurement<int>(_lastAvailableWorkers));

    public JobDispatcherService(
        ISliceJobRepository jobRepository,
        IWorkerRepository workerRepository,
        ISliceJobEventService eventService,
        IUnifiedLoggingService logger,
            IHttpClientFactory httpClientFactory,
            RetryOptions retryOptions)
    {
        _jobRepository = jobRepository ?? throw new ArgumentNullException(nameof(jobRepository));
        _workerRepository = workerRepository ?? throw new ArgumentNullException(nameof(workerRepository));
        _eventService = eventService ?? throw new ArgumentNullException(nameof(eventService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _retryOptions = retryOptions ?? throw new ArgumentNullException(nameof(retryOptions));
    }

    public async Task<bool> DispatchNextJobAsync(CancellationToken cancellationToken = default)
    {
        // Get the next queued job (highest priority first, then FIFO)
        IReadOnlyList<SliceJob> queuedJobs = await _jobRepository.GetQueuedJobsAsync(1);
        if (queuedJobs.Count == 0)
        {
            return false; // No jobs in queue
        }

        SliceJob job = queuedJobs[0];
        return await DispatchJobAsync(job.Id, cancellationToken);
    }

    public async Task<bool> DispatchJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Get job details
            SliceJob? job = await _jobRepository.GetByIdAsync(jobId);
            if (job == null)
            {
                _logger.LogWarning($"Cannot dispatch job {jobId}: job not found");
                return false;
            }

            if (job.Status != SliceJobStatus.Queued)
            {
                _logger.LogWarning($"Cannot dispatch job {jobId}: job is not in Queued status (current: {job.Status})");
                return false;
            }

            // Find the best worker for this job
            Worker? worker = await FindBestWorkerForJobAsync(job, cancellationToken);
            if (worker == null)
            {
                _logger.LogDebug($"No suitable worker found for job {jobId}");
                var noWorkerTags = new System.Diagnostics.TagList { { "outcome", "failed" } };
                _dispatchDurationMs.Record(sw.ElapsedMilliseconds, noWorkerTags);
                var noWorkerReasonTags = new System.Diagnostics.TagList { { "reason", "no_worker" } };
                _jobsDispatchFailed.Add(1, noWorkerReasonTags);
                return false;
            }

            // Send job to worker via HTTP API
            bool success = await SendJobToWorkerAsync(worker, job, cancellationToken);
            if (!success)
            {
                _logger.LogWarning($"Failed to send job {jobId} to worker {worker.Id}");
                var sendFailedTags = new System.Diagnostics.TagList { { "outcome", "failed" } };
                _dispatchDurationMs.Record(sw.ElapsedMilliseconds, sendFailedTags);
                var sendFailedReasonTags = new System.Diagnostics.TagList { { "reason", "send_failed" } };
                _jobsDispatchFailed.Add(1, sendFailedReasonTags);
                return false;
            }

            // Update job status to Processing
            await _jobRepository.MarkStartedAsync(jobId, worker.Id);
            await _jobRepository.SaveChangesAsync();

            // Update worker active jobs
            await _workerRepository.IncrementActiveJobsAsync(worker.Id);
            await _workerRepository.SaveChangesAsync();

            // Broadcast job started event
            SliceJob? updatedJob = await _jobRepository.GetByIdAsync(jobId);
            if (updatedJob != null)
            {
                await _eventService.NotifyJobStartedAsync(updatedJob, cancellationToken);
            }

            _logger.LogInformation($"Job {jobId} dispatched to worker {worker.Id} ({worker.Name})");
            var successTags = new System.Diagnostics.TagList { { "outcome", "success" } };
            _dispatchDurationMs.Record(sw.ElapsedMilliseconds, successTags);
            _jobsDispatched.Add(1);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error dispatching job {jobId}: {ex.Message}");
            var errorTags = new System.Diagnostics.TagList { { "outcome", "error" } };
            _dispatchDurationMs.Record(sw.ElapsedMilliseconds, errorTags);
            var exceptionReasonTags = new System.Diagnostics.TagList { { "reason", "exception" } };
            _jobsDispatchFailed.Add(1, exceptionReasonTags);
            return false;
        }
    }

    public async Task<Worker?> FindBestWorkerForJobAsync(SliceJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        // Parse required capabilities
        string[]? requiredCapabilities = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(job.RequiredCapabilitiesJson))
            {
                requiredCapabilities = JsonSerializer.Deserialize<string[]>(job.RequiredCapabilitiesJson);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning($"Failed to parse capabilities for job {job.Id}: {ex.Message}");
        }

        // Get workers with matching capabilities
        IReadOnlyList<Worker> availableWorkers = requiredCapabilities == null || requiredCapabilities.Length == 0
            ? await _workerRepository.GetAvailableWorkersAsync(50)
            : await _workerRepository.GetWorkersByCapabilitiesAsync(requiredCapabilities, 50);

        // Filter stale workers by heartbeat (default 120s, override via SLICER_WORKER_STALE_SECONDS env)
        int staleSeconds = 120;
        try
        {
            var envVal = Environment.GetEnvironmentVariable("SLICER_WORKER_STALE_SECONDS");
            if (!string.IsNullOrWhiteSpace(envVal) && int.TryParse(envVal, out int parsed) && parsed >= 30 && parsed <= 3600)
            {
                staleSeconds = parsed;
            }
        }
        catch { }
        DateTime cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(staleSeconds);
        availableWorkers = availableWorkers.Where(w => w.LastHeartbeat == null || w.LastHeartbeat >= cutoff).ToList();
        _lastAvailableWorkers = availableWorkers.Count;

        if (availableWorkers.Count == 0)
        {
            return null;
        }

        // Score workers and select the best one
        Worker? bestWorker = SelectBestWorker(availableWorkers, job);
        return bestWorker;
    }

    private Worker? SelectBestWorker(IReadOnlyList<Worker> workers, SliceJob job)
    {
        if (workers.Count == 0)
        {
            return null;
        }

        if (workers.Count == 1)
        {
            return workers[0];
        }

        // Scoring algorithm:
        // - More free slots = better
        // - Fewer active jobs = better
        // - Faster average processing time = better
        // - Worker with matching slicer engine capability = bonus

        Worker? bestWorker = null;
        double bestScore = double.MinValue;

        foreach (Worker worker in workers)
        {
            double score = CalculateWorkerScore(worker, job);
            if (score > bestScore)
            {
                bestScore = score;
                bestWorker = worker;
            }
        }

        return bestWorker;
    }

    private double CalculateWorkerScore(Worker worker, SliceJob job)
    {
        double score = 0;

        // Free slots score (0-10 points, more is better)
        double capacityRatio = worker.FreeSlots / (double)Math.Max(1, worker.TotalSlots);
        score += capacityRatio * 10;

        // Active jobs score (0-5 points, fewer is better)
        double loadRatio = 1.0 - (worker.ActiveJobs / (double)Math.Max(1, worker.TotalSlots));
        score += loadRatio * 5;

        // Processing speed score (0-5 points, faster is better)
        if (worker.AverageProcessingTimeSeconds.HasValue && worker.AverageProcessingTimeSeconds.Value > 0)
        {
            // Normalize: assume 300 seconds is baseline, faster gets bonus
            double speedScore = Math.Max(0, (300 - worker.AverageProcessingTimeSeconds.Value) / 300 * 5);
            score += speedScore;
        }

        // Success rate score (0-5 points)
        int totalJobs = worker.CompletedJobs + worker.FailedJobs;
        if (totalJobs > 0)
        {
            double successRate = worker.CompletedJobs / (double)totalJobs;
            score += successRate * 5;
        }

        // Capability match bonus (5 points if worker has specific slicer capability)
        try
        {
            string[]? workerCapabilities = JsonSerializer.Deserialize<string[]>(worker.CapabilitiesJson);
            if (workerCapabilities != null)
            {
                // Get slicer engine name (e.g., "orcaslicer", "prusaslicer")
                string slicerName = GetSlicerEngineName(job.SlicerEngine);
                if (workerCapabilities.Contains(slicerName, StringComparer.OrdinalIgnoreCase))
                {
                    score += 5;
                }
            }
        }
        catch
        {
            // Ignore capability parsing errors
        }

        return score;
    }

    private string GetSlicerEngineName(int slicerEngine)
    {
        return slicerEngine switch
        {
            0 => "orcaslicer",
            1 => "prusaslicer",
            _ => "unknown"
        };
    }

    private async Task<bool> SendJobToWorkerAsync(Worker worker, SliceJob job, CancellationToken cancellationToken)
    {
           int maxAttempts = _retryOptions.MaxAttempts;
           int baseDelayMs = _retryOptions.BaseDelayMs;
           double multiplier = _retryOptions.Multiplier;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                HttpClient client = _httpClientFactory.CreateClient();

                // Build job request payload
                var payload = new
                {
                    jobId = job.Id,
                    modelFileUrl = job.ModelFileUrl,
                    modelFileName = job.ModelFileName,
                    slicerEngine = job.SlicerEngine,
                    slicerProfile = job.SlicerProfileJson,
                    priority = job.Priority
                };

                // Send POST request to worker's /api/jobs endpoint
                string endpoint = $"{worker.EndpointUrl.TrimEnd('/')}/api/jobs";
                HttpResponseMessage response = await client.PostAsJsonAsync(endpoint, payload, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug($"Successfully sent job {job.Id} to worker {worker.Id} at {endpoint} (attempt {attempt})");
                    return true;
                }
                else if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
                {
                    // Transient error - retry
                    string error = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning($"Transient error from worker {worker.Id} for job {job.Id} (attempt {attempt}/{maxAttempts}): {response.StatusCode} - {error}");
                    
                    if (attempt < maxAttempts)
                    {
                            int delayMs = (int)(baseDelayMs * Math.Pow(multiplier, attempt - 1));
                        await Task.Delay(delayMs, cancellationToken);
                        continue;
                    }
                    return false;
                }
                else
                {
                    // Non-transient error - don't retry
                    string error = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning($"Worker {worker.Id} rejected job {job.Id}: {response.StatusCode} - {error}");
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning($"HTTP error sending job {job.Id} to worker {worker.Id} (attempt {attempt}/{maxAttempts}): {ex.Message}");
                if (attempt < maxAttempts)
                {
                        int delayMs = (int)(baseDelayMs * Math.Pow(multiplier, attempt - 1));
                    await Task.Delay(delayMs, cancellationToken);
                    continue;
                }
                return false;
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning($"Timeout sending job {job.Id} to worker {worker.Id}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Unexpected error sending job {job.Id} to worker {worker.Id}: {ex.Message}");
                return false;
            }
        }
        return false;
    }
}
