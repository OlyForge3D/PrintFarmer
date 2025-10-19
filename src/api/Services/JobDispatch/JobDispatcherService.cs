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

    public JobDispatcherService(
        ISliceJobRepository jobRepository,
        IWorkerRepository workerRepository,
        ISliceJobEventService eventService,
        IUnifiedLoggingService logger,
        IHttpClientFactory httpClientFactory)
    {
        _jobRepository = jobRepository ?? throw new ArgumentNullException(nameof(jobRepository));
        _workerRepository = workerRepository ?? throw new ArgumentNullException(nameof(workerRepository));
        _eventService = eventService ?? throw new ArgumentNullException(nameof(eventService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
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
            return false;
        }

        try
        {
            // Send job to worker via HTTP API
            bool success = await SendJobToWorkerAsync(worker, job, cancellationToken);
            if (!success)
            {
                _logger.LogWarning($"Failed to send job {jobId} to worker {worker.Id}");
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
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error dispatching job {jobId} to worker {worker.Id}: {ex.Message}");
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
                _logger.LogDebug($"Successfully sent job {job.Id} to worker {worker.Id} at {endpoint}");
                return true;
            }
            else
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning($"Worker {worker.Id} rejected job {job.Id}: {response.StatusCode} - {error}");
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning($"HTTP error sending job {job.Id} to worker {worker.Id}: {ex.Message}");
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
}
