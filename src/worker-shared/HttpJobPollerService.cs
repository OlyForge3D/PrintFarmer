using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Worker.Core;

/// <summary>
/// HTTP-based job poller that claims jobs from the API via POST /api/slice/claim,
/// processes them through the slicing pipeline, uploads artifacts, and completes the job.
/// This replaces the Redis-based BaseQueueConsumerService to integrate with the SQL database queue.
/// </summary>
/// <param name="httpClientFactory">Factory for creating HTTP clients to communicate with the API.</param>
/// <param name="serviceProvider">Service provider for creating scoped services during job processing.</param>
/// <param name="logger">Unified logging service for telemetry and diagnostics.</param>
/// <param name="workerState">Service for tracking worker state and active job counts.</param>
/// <param name="configuration">Configuration for worker settings such as API URL and poll interval.</param>
public abstract class HttpJobPollerService(
    IHttpClientFactory httpClientFactory,
    IServiceProvider serviceProvider,
    ILogger<HttpJobPollerService> logger,
    IWorkerStateService workerState,
    IConfiguration configuration) : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ILogger<HttpJobPollerService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IWorkerStateService _workerState = workerState ?? throw new ArgumentNullException(nameof(workerState));
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly Guid _workerId = ResolveWorkerId(configuration);
    private readonly string? _workerApiKey = configuration["WorkerAuth:SharedApiKey"]
                                              ?? configuration["WorkerAuth:SharedKey"]
                                              ?? Environment.GetEnvironmentVariable("WORKER_SHARED_API_KEY");

    private static Guid ResolveWorkerId(IConfiguration configuration)
    {
        string? instanceId = configuration["Worker:InstanceId"];
        if (!string.IsNullOrWhiteSpace(instanceId) && Guid.TryParse(instanceId, out Guid parsed))
        {
            return parsed;
        }

        return Guid.NewGuid();
    }

    /// <summary>
    /// Derived classes implement this to execute the slicing pipeline.
    /// </summary>
    /// <param name="job">The distributed slicing job containing model file and configuration.</param>
    /// <param name="scopeServices">Scoped service provider for resolving dependencies during pipeline execution.</param>
    /// <param name="ct">Cancellation token to observe for cancellation requests.</param>
    /// <returns>A task containing the slicing result with output file path and metadata.</returns>
    protected abstract Task<SlicingResult> ExecutePipelineAsync(DistributedSlicingJob job, IServiceProvider scopeServices, CancellationToken ct);

    /// <summary>
    /// Derived classes specify which capabilities this worker provides (e.g., ["orcaslicer", "stl-processing"]).
    /// </summary>
    /// <returns>An array of capability strings that this worker supports.</returns>
    protected abstract string[] GetWorkerCapabilities();

    private const string DefaultApiBaseUrl = "http://localhost:5245"; // fallback dev URL

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Resolve API base URL — prefer unified SlicerApi:BaseUrl, then legacy keys.
        string apiBaseUrl = _configuration["SlicerApi:BaseUrl"]
                              ?? _configuration["Worker:ApiBaseUrl"]
                              ?? Environment.GetEnvironmentVariable("WORKER_API_BASE_URL")
                              ?? DefaultApiBaseUrl;
        int pollIntervalSeconds = int.Parse(_configuration["Worker:PollIntervalSeconds"] ?? "5");
        int leaseDurationSeconds = int.Parse(_configuration["Worker:LeaseDurationSeconds"] ?? "300"); // 5 minutes default

        _logger.LogInformation("Worker {WorkerId} HTTP job poller starting. API={ApiBaseUrl} PollInterval={PollIntervalSeconds}s Lease={LeaseDurationSeconds}s", _workerId, apiBaseUrl, pollIntervalSeconds, leaseDurationSeconds);
        _logger.LogInformation("Worker capabilities: {Value0}", string.Join(", ", GetWorkerCapabilities()));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using HttpClient httpClient = _httpClientFactory.CreateClient();
                httpClient.BaseAddress = new Uri(apiBaseUrl);
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                if (!string.IsNullOrWhiteSpace(_workerApiKey))
                {
                    httpClient.DefaultRequestHeaders.Add("X-Worker-Key", _workerApiKey);
                }

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
                    _logger.LogWarning("Failed to claim job: {ClaimResponseStatusCode}", claimResponse.StatusCode);
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

                // Resolve profile names from SlicerProfileJson into full SlicerProfileDto
                job.Profile = await ResolveProfileFromJsonAsync(jobStatus.SlicerProfileJson, stoppingToken);

                _workerState.IncrementActiveJobs();
                _logger.LogInformation("Claimed job {JobId}, starting processing", job.Id);

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

        _logger.LogInformation("Worker {WorkerId} HTTP job poller stopped", _workerId);
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

                _ = Task.Run(
                    async () =>
                {
                    while (!localLinkedCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            RenewLeaseRequest renewReq = new RenewLeaseRequest { LeaseDurationSeconds = leaseDurationSeconds };
                            HttpResponseMessage resp = await httpClient.PostAsJsonAsync($"/api/slice/{job.Id}/renew", renewReq, localLinkedCts.Token);
                            if (!resp.IsSuccessStatusCode)
                            {
                                _logger.LogDebug("Lease renew for job {JobId} returned {RespStatusCode}", job.Id, resp.StatusCode);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to renew lease for job {JobId}", job.Id);
                        }

                        await Task.Delay(TimeSpan.FromSeconds(renewIntervalSeconds), localLinkedCts.Token);
                    }
                }, localLinkedCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to start lease renew loop for job {JobId}", job.Id);
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

            _logger.LogInformation("Job {JobId} slicing completed in {TotalSeconds:F1}s", job.Id, (DateTime.UtcNow - start).TotalSeconds);

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

            _logger.LogInformation("Job {JobId} completed successfully with {ArtifactIdsCount} artifacts", job.Id, artifactIds.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Job {JobId} cancelled", job.Id);

            // Job will timeout and be reassigned by the API's error recovery system
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed: {Message}", job.Id, ex.Message);

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
            catch
            {
            }

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
                _logger.LogDebug("Progress update for job {JobId} returned {RespStatusCode}", jobId, resp.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send progress update for job {JobId}", jobId);
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

        _logger.LogInformation("Uploading G-code artifact from {GcodeFilePath}", gcodeFilePath);

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
        _logger.LogInformation("Uploaded G-code artifact: {ArtifactResponseId} ({GcodeBytesLength} bytes)", artifactResponse.Id, gcodeBytes.Length);

        // Phase 3E: Upload additional artifacts (thumbnails, metadata) from result.Metadata
        // Requires ArtifactsController endpoint and SlicingArtifactKeys conventions.
        // See .squad/decisions/inbox/dallas-blocked-items-architecture.md for design.
        return artifactIds;
    }

    /// <summary>
    /// Resolves profile names from SlicerProfileJson into a full SlicerProfileDto
    /// by looking up cached profiles from the ISlicerProfilesService and applying user overrides.
    ///
    /// Expected JSON format:
    /// {
    ///   "machineProfileName": "Phrozen Arco 0.4 nozzle (0.4mm)",
    ///   "filamentProfileName": "Generic PLA @System",
    ///   "processProfileName": "0.20mm Standard @Phrozen Arco",
    ///   "overrides": { "infillDensity": 30, "printSpeed": 100 }
    /// }
    /// </summary>
    private async Task<SlicerProfileDto?> ResolveProfileFromJsonAsync(string? slicerProfileJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slicerProfileJson))
        {
            _logger.LogWarning("SlicerProfileJson is null or empty — cannot resolve profiles");
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(slicerProfileJson);
            JsonElement root = doc.RootElement;

            string machineProfileName = root.TryGetProperty("machineProfileName", out JsonElement machElem) ? machElem.GetString() ?? string.Empty : string.Empty;
            string filamentProfileName = root.TryGetProperty("filamentProfileName", out JsonElement filElem) ? filElem.GetString() ?? string.Empty : string.Empty;
            string processProfileName = root.TryGetProperty("processProfileName", out JsonElement procElem) ? procElem.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrEmpty(machineProfileName) && string.IsNullOrEmpty(filamentProfileName) && string.IsNullOrEmpty(processProfileName))
            {
                _logger.LogWarning("No profile names found in SlicerProfileJson — cannot resolve profiles");
                return null;
            }

            ISlicerProfilesService? profilesService = _serviceProvider.GetService<ISlicerProfilesService>();
            if (profilesService == null)
            {
                _logger.LogWarning("ISlicerProfilesService not available — cannot resolve profiles");
                return null;
            }

            SlicerProfileDto profile = new();

            // Resolve machine profile by name
            if (!string.IsNullOrEmpty(machineProfileName))
            {
                IList<MachineProfileDto> machines = await profilesService.ListAvailableMachineProfilesAsync(ct);
                profile.MachineProfile = machines.FirstOrDefault(m =>
                    string.Equals(m.Name, machineProfileName, StringComparison.OrdinalIgnoreCase));
                if (profile.MachineProfile == null)
                {
                    _logger.LogWarning("Machine profile '{MachineProfileName}' not found in {MachinesCount} cached profiles", machineProfileName, machines.Count);
                }
                else
                {
                    _logger.LogInformation("Resolved machine profile: {MachineName}", profile.MachineProfile.Name);
                }
            }

            // Resolve filament profile by name
            if (!string.IsNullOrEmpty(filamentProfileName))
            {
                IList<FilamentProfileDto> filaments = await profilesService.ListAvailableFilamentProfilesAsync(ct);
                profile.FilamentProfile = filaments.FirstOrDefault(f =>
                    string.Equals(f.Name, filamentProfileName, StringComparison.OrdinalIgnoreCase));
                if (profile.FilamentProfile == null)
                {
                    _logger.LogWarning("Filament profile '{FilamentProfileName}' not found in {FilamentsCount} cached profiles", filamentProfileName, filaments.Count);
                }
                else
                {
                    _logger.LogInformation("Resolved filament profile: {FilamentName}", profile.FilamentProfile.Name);
                }
            }

            // Resolve process profile by name
            if (!string.IsNullOrEmpty(processProfileName))
            {
                IList<ProcessProfileDto> processes = await profilesService.ListAvailableProcessProfilesAsync(ct);
                profile.ProcessProfile = processes.FirstOrDefault(p =>
                    string.Equals(p.Name, processProfileName, StringComparison.OrdinalIgnoreCase));
                if (profile.ProcessProfile == null)
                {
                    _logger.LogWarning("Process profile '{ProcessProfileName}' not found in {ProcessesCount} cached profiles", processProfileName, processes.Count);
                }
                else
                {
                    _logger.LogInformation("Resolved process profile: {ProcessName}", profile.ProcessProfile.Name);
                }
            }

            // Apply user overrides to the process profile settings
            if (profile.ProcessProfile != null && root.TryGetProperty("overrides", out JsonElement overridesElem))
            {
                foreach (JsonProperty prop in overridesElem.EnumerateObject())
                {
                    object value = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Number when prop.Value.TryGetInt32(out int intVal) => intVal,
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                        _ => prop.Value.ToString()
                    };
                    profile.ProcessProfile.Settings[prop.Name] = value;
                }

                _logger.LogInformation("Applied {OverrideCount} user overrides to process profile", overridesElem.EnumerateObject().Count());
            }

            return profile;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse SlicerProfileJson: {SlicerProfileJson}", slicerProfileJson);
            return null;
        }
    }
}
