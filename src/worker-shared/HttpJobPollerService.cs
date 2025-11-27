using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;
using Farm.Web.Shared.Contracts.Slicing; // ClaimJobRequest & completion DTOs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Slicer.Worker.Core;

/// <summary>
/// HTTP-based job poller that claims jobs from the API via POST /api/slice/claim,
/// processes them through the slicing pipeline, uploads artifacts, and completes the job.
/// This replaces the Redis-based BaseQueueConsumerService to integrate with the SQL database queue.
/// </summary>
public abstract class HttpJobPollerService(
    IHttpClientFactory httpClientFactory,
    IServiceProvider serviceProvider,
    IUnifiedLoggingService logger,
    IWorkerStateService workerState,
    IConfiguration configuration) : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly IUnifiedLoggingService _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IWorkerStateService _workerState = workerState ?? throw new ArgumentNullException(nameof(workerState));
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly Guid _workerId = Guid.NewGuid();

    /// <summary>
    /// Derived classes implement this to execute the slicing pipeline
    /// </summary>
    protected abstract Task<SlicingResult> ExecutePipelineAsync(DistributedSlicingJob job, IServiceProvider scopeServices, CancellationToken ct);

    /// <summary>
    /// Derived classes specify which capabilities this worker provides (e.g., ["orcaslicer", "stl-processing"])
    /// </summary>
    protected abstract string[] GetWorkerCapabilities();

    private const string DefaultApiBaseUrl = "http://localhost:5245"; // fallback dev URL

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Resolve API base URL from hierarchical config or environment variable, fall back to dev default.
        string apiBaseUrl = _configuration["Worker:ApiBaseUrl"]
                              ?? Environment.GetEnvironmentVariable("WORKER_API_BASE_URL")
                              ?? DefaultApiBaseUrl;
        int pollIntervalSeconds = int.Parse(_configuration["Worker:PollIntervalSeconds"] ?? "5");
        int leaseDurationSeconds = int.Parse(_configuration["Worker:LeaseDurationSeconds"] ?? "300"); // 5 minutes default

        _logger.LogInformation($"Worker {_workerId} HTTP job poller starting. API={apiBaseUrl} PollInterval={pollIntervalSeconds}s Lease={leaseDurationSeconds}s");
        _logger.LogInformation($"Worker capabilities: {string.Join(", ", GetWorkerCapabilities())}");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using HttpClient httpClient = _httpClientFactory.CreateClient();
                httpClient.BaseAddress = new Uri(apiBaseUrl);
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                // Attempt to claim a job
                ClaimJobRequest claimRequest = new ClaimJobRequest
                {
                    WorkerId = _workerId,
                    Capabilities = GetWorkerCapabilities(),
                    LeaseDurationSeconds = leaseDurationSeconds
                };

                HttpResponseMessage claimResponse = await httpClient.PostAsJsonAsync("/api/slice/claim", claimRequest, stoppingToken);

                if (claimResponse.StatusCode == HttpStatusCode.NoContent)
                {
                    // No jobs available, wait before polling again
                    await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), stoppingToken);
                    continue;
                }

                if (!claimResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"Failed to claim job: {claimResponse.StatusCode}");
                    await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), stoppingToken);
                    continue;
                }

                SliceJobStatusResponse? jobStatus = await claimResponse.Content.ReadFromJsonAsync<SliceJobStatusResponse>(stoppingToken);
                if (jobStatus == null)
                {
                    _logger.LogWarning("Claimed job but received null response");
                    await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), stoppingToken);
                    continue;
                }

                // Convert SliceJobStatusResponse to DistributedSlicingJob for pipeline
                // Map claimed job to DistributedSlicingJob (feed actual metadata to pipeline)
                SlicerEngineType engineEnum = (SlicerEngineType)jobStatus.SlicerEngine;
                DistributedSlicingJob job = new DistributedSlicingJob
                {
                    Id = jobStatus.Id,
                    WorkerId = _workerId.ToString(),
                    ModelFileUrl = Uri.TryCreate(jobStatus.ModelFileUrl, UriKind.RelativeOrAbsolute, out Uri? tmp) ? tmp : new Uri("about:blank", UriKind.RelativeOrAbsolute),
                    ModelFileName = jobStatus.ModelFileName,
                    EngineType = engineEnum,
                    SlicerEngine = engineEnum.ToString(),
                    Status = SlicingJobStatus.Slicing, // in-progress mapping
                    StartedAt = DateTime.UtcNow
                };

                _workerState.IncrementActiveJobs();
                _logger.LogInformation($"Claimed job {job.Id}, starting processing");

                // Emit initial progress (0%)
                await TrySendProgressAsync(httpClient, job.Id, 0, "Starting slicing", stoppingToken);

                await HandleJobAsync(job, httpClient, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Job poller cancellation requested");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in job polling loop; backing off");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation($"Worker {_workerId} HTTP job poller stopped");
    }

    private async Task HandleJobAsync(DistributedSlicingJob job, HttpClient httpClient, CancellationToken ct)
    {
        DateTime start = DateTime.UtcNow;
        CancellationTokenSource? localLinkedCts = null;
        try
        {
            using IServiceScope scope = _serviceProvider.CreateScope();

            // Start a lease-renewal loop to prevent the API from reclaiming the job while we're actively processing.
            try
            {
                localLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                int leaseDurationSeconds = int.Parse(_configuration["Worker:LeaseDurationSeconds"] ?? "300");
                int renewIntervalSeconds = Math.Max(10, leaseDurationSeconds / 3);

                _ = Task.Run(async () =>
                {
                    while (!localLinkedCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            RenewLeaseRequest renewReq = new RenewLeaseRequest { LeaseDurationSeconds = leaseDurationSeconds };
                            HttpResponseMessage resp = await httpClient.PostAsJsonAsync($"/api/slice/{job.Id}/renew", renewReq, localLinkedCts.Token);
                            if (!resp.IsSuccessStatusCode)
                            {
                                _logger.LogDebug($"Lease renew for job {job.Id} returned {resp.StatusCode}");
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, $"Failed to renew lease for job {job.Id}");
                        }

                        await Task.Delay(TimeSpan.FromSeconds(renewIntervalSeconds), localLinkedCts.Token);
                    }
                }, localLinkedCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"Failed to start lease renew loop for job {job.Id}");
                localLinkedCts?.Dispose();
                localLinkedCts = null;
            }

            // Execute the slicing pipeline (downloads STL, runs slicer, generates G-code)
            SlicingResult result = await ExecutePipelineAsync(job, scope.ServiceProvider, ct);
            if (!result.Success)
            {
                throw new InvalidOperationException("Slicing pipeline reported failure");
            }

            // Mid-progress update (pipeline finished but artifacts pending)
            // Use heuristic progress since SlicingResult doesn't expose granular percentage yet.
            await TrySendProgressAsync(httpClient, job.Id, 85, "Slicing complete, uploading artifacts", ct);

            _logger.LogInformation($"Job {job.Id} slicing completed in {(DateTime.UtcNow - start).TotalSeconds:F1}s");

            // Upload artifacts (G-code file and any metadata)
            List<Guid> artifactIds = await UploadArtifactsAsync(job, result, httpClient, ct);

            // Complete the job with artifact references
            CompleteSliceJobRequest completeRequest = new CompleteSliceJobRequest
            {
                PrimaryArtifactId = artifactIds[0],
                AdditionalArtifactIds = artifactIds.Skip(1).ToArray(),
                EstimatedPrintTimeSeconds = (int?)Math.Round(result.EstimatedPrintTimeSeconds),
                FilamentUsedGrams = (decimal?)Math.Round(result.EstimatedFilamentUsageGrams, 2),
                LogText = result.Metadata.TryGetValue("SlicerLog", out string? logObj) ? logObj?.ToString() : null
            };

            HttpResponseMessage completeResponse = await httpClient.PostAsJsonAsync($"/api/slice/{job.Id}/complete", completeRequest, ct);

            if (!completeResponse.IsSuccessStatusCode)
            {
                string errorContent = await completeResponse.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Failed to complete job: {completeResponse.StatusCode} - {errorContent}");
            }

            _logger.LogInformation($"Job {job.Id} completed successfully with {artifactIds.Count} artifacts");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning($"Job {job.Id} cancelled");
            // Job will timeout and be reassigned by the API's error recovery system
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Job {job.Id} failed: {ex.Message}");
            // Job will timeout and be reassigned by the API's error recovery system
        }
        finally
        {

            // Stop lease renew task if running by cancelling and disposing localLinkedCts
            try
            {
                if (localLinkedCts != null)
                {
                    await localLinkedCts.CancelAsync();
                    localLinkedCts.Dispose();
                }
            }
            catch { }

            _workerState.DecrementActiveJobs();
        }
    }

    private async Task TrySendProgressAsync(HttpClient client, Guid jobId, int percent, string message, CancellationToken ct)
    {
        try
        {
            SliceJobProgressUpdateRequest progressReq = new SliceJobProgressUpdateRequest
            {
                ProgressPercent = percent,
                ProgressMessage = message
            };
            HttpResponseMessage resp = await client.PostAsJsonAsync($"/api/slice/{jobId}/progress", progressReq, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug($"Progress update for job {jobId} returned {resp.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, $"Failed to send progress update for job {jobId}");
        }
    }

    private async Task<List<Guid>> UploadArtifactsAsync(DistributedSlicingJob job, SlicingResult result, HttpClient httpClient, CancellationToken ct)
    {
        List<Guid> artifactIds = new List<Guid>();

        // Extract the local file path from the result URL
        string? gcodeFilePath = null;
        if (result.ResultFileUrl != null)
        {
            // ResultFileUrl might be a file:// URI or just a local path
            if (result.ResultFileUrl.IsAbsoluteUri && result.ResultFileUrl.Scheme == "file")
            {
                gcodeFilePath = result.ResultFileUrl.LocalPath;
            }
            else if (result.ResultFileUrl.IsAbsoluteUri)
            {
                // Might be a relative path stored as URI
                gcodeFilePath = result.ResultFileUrl.ToString();
            }
            else
            {
                gcodeFilePath = result.ResultFileUrl.OriginalString;
            }
        }

        if (string.IsNullOrEmpty(gcodeFilePath) || !File.Exists(gcodeFilePath))
        {
            throw new InvalidOperationException($"G-code file not found at expected path: {gcodeFilePath}");
        }

        _logger.LogInformation($"Uploading G-code artifact from {gcodeFilePath}");

        // Upload the primary G-code file
        using MultipartFormDataContent gcodeContent = new MultipartFormDataContent();
        gcodeContent.Add(new StringContent(job.Id.ToString()), "jobId");
        gcodeContent.Add(new StringContent("gcode"), "kind");
        gcodeContent.Add(new StringContent(_workerId.ToString()), "workerId");

        byte[] gcodeBytes = await File.ReadAllBytesAsync(gcodeFilePath, ct);
        gcodeContent.Add(new ByteArrayContent(gcodeBytes), "file", Path.GetFileName(gcodeFilePath));

        HttpResponseMessage uploadResponse = await httpClient.PostAsync("/api/artifacts", gcodeContent, ct);

        if (!uploadResponse.IsSuccessStatusCode)
        {
            string errorContent = await uploadResponse.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Failed to upload G-code artifact: {uploadResponse.StatusCode} - {errorContent}");
        }

        ArtifactResponse? artifactResponse = await uploadResponse.Content.ReadFromJsonAsync<ArtifactResponse>(ct);
        if (artifactResponse?.Id == null)
        {
            throw new InvalidOperationException("Artifact upload succeeded but no ID returned");
        }

        artifactIds.Add(artifactResponse.Id);
        _logger.LogInformation($"Uploaded G-code artifact: {artifactResponse.Id} ({gcodeBytes.Length} bytes)");

        // TODO: Upload additional artifacts (thumbnails, metadata, etc.) if present in result.Metadata

        return artifactIds;
    }
}

/// <summary>
/// Response from artifact upload endpoint
/// </summary>
public class ArtifactResponse
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Sha256Hash { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
