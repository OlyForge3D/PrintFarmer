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

        _logger.LogInformation("HTTP job poller starting. API={ApiBaseUrl} PollInterval={PollIntervalSeconds}s Lease={LeaseDurationSeconds}s", apiBaseUrl, pollIntervalSeconds, leaseDurationSeconds);
        _logger.LogInformation("Worker capabilities: {Value0}", string.Join(", ", GetWorkerCapabilities()));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                WorkerState currentWorkerState = _workerState.GetWorkerState();
                Guid? registeredServiceId = currentWorkerState.RegisteredServiceId;
                if (registeredServiceId is null ||
                    string.IsNullOrWhiteSpace(currentWorkerState.RegisteredServiceApiKey))
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), stoppingToken);
                    continue;
                }

                using HttpClient httpClient = _httpClientFactory.CreateClient();
                httpClient.BaseAddress = new Uri(apiBaseUrl);
                httpClient.Timeout = TimeSpan.FromSeconds(30);
                httpClient.DefaultRequestHeaders.Add("X-Worker-Key", currentWorkerState.RegisteredServiceApiKey);
                httpClient.DefaultRequestHeaders.Add("X-Worker-Id", registeredServiceId.Value.ToString());

                // Attempt to claim a job
                ClaimJobRequest claimRequest = new ClaimJobRequest
                {
                    WorkerId = registeredServiceId.Value,
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

                WorkerSliceJobResponse? jobStatus =
                    await claimResponse.Content.ReadFromJsonAsync<WorkerSliceJobResponse>(stoppingToken);
                if (jobStatus == null)
                {
                    _logger.LogWarning("Claimed job but received null response");
                    await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), stoppingToken);
                    continue;
                }

                // Convert the worker-only claim contract to the pipeline model.
                // Map claimed job to DistributedSlicingJob (feed actual metadata to pipeline)
                SlicerEngineType engineEnum = (SlicerEngineType)jobStatus.SlicerEngine;
                DistributedSlicingJob job = new DistributedSlicingJob
                {
                    Id = jobStatus.Id,
                    WorkerId = registeredServiceId.Value.ToString(),
                    ModelFileUrl = new Uri(httpClient.BaseAddress, jobStatus.ModelFileUrl),
                    ModelFileName = jobStatus.ModelFileName,
                    EngineType = engineEnum,
                    SlicerEngine = engineEnum.ToString(),
                    Status = SlicingJobStatus.Slicing, // in-progress mapping
                    StartedAt = DateTime.UtcNow,
                    ModelTransformJson = jobStatus.ModelTransformJson,
                    ModelFileUrls = jobStatus.ModelFileUrls,
                    ModelFileTransforms = jobStatus.ModelFileTransforms,
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

        _logger.LogInformation("HTTP job poller stopped");
    }

    private async Task HandleJobAsync(DistributedSlicingJob job, HttpClient httpClient, CancellationToken ct)
    {
        DateTime start = DateTime.UtcNow;
        CancellationTokenSource? localLinkedCts = null;
        SlicingResult? result = null;
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
                            HttpResponseMessage resp = await httpClient.PostAsJsonAsync($"/api/slice/{job.Id}/renew-lease", renewReq, localLinkedCts.Token);
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
            result = await ExecutePipelineAsync(job, scope.ServiceProvider, ct);
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

            // Report failure to the API so the job doesn't sit in Processing until lease expires
            await TryReportFailureAsync(httpClient, job.Id, ex.Message, ct);
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

            TryCleanupLocalResult(job.Id, result);
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

    private async Task TryReportFailureAsync(HttpClient client, Guid jobId, string errorMessage, CancellationToken ct)
    {
        try
        {
            string truncated = errorMessage.Length > 1000 ? errorMessage[..1000] : errorMessage;
            var failReq = new { errorMessage = truncated };

            HttpResponseMessage resp = await client.PostAsJsonAsync($"/api/slice/{jobId}/fail", failReq, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Fail report for job {JobId} returned {RespStatusCode}", jobId, resp.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to report failure for job {JobId}", jobId);
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

        // Upload the primary G-code file without buffering the full artifact in memory.
        await using FileStream gcodeStream = OpenArtifactFileStream(gcodeFilePath);
        using MultipartFormDataContent gcodeContent = new MultipartFormDataContent();
        StreamContent gcodeFileContent = new StreamContent(gcodeStream);
        gcodeFileContent.Headers.ContentLength = gcodeStream.Length;
        gcodeContent.Add(gcodeFileContent, "file", Path.GetFileName(gcodeFilePath));

        using HttpResponseMessage uploadResponse =
            await httpClient.PostAsync($"/api/slice/{job.Id}/artifacts", gcodeContent, ct);

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
        _logger.LogInformation("Uploaded G-code artifact: {ArtifactResponseId} ({GcodeBytesLength} bytes)", artifactResponse.Id, gcodeStream.Length);

        // Phase 3E: Upload additional artifacts (thumbnails, metadata) from result.Metadata
        // Requires ArtifactsController endpoint and SlicingArtifactKeys conventions.
        // See .squad/decisions/inbox/dallas-blocked-items-architecture.md for design.
        return artifactIds;
    }

    /// <summary>
    /// Opens an artifact for asynchronous sequential upload without materializing it in memory.
    /// </summary>
    internal static FileStream OpenArtifactFileStream(string filePath)
    {
        return new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
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
    ///   "overrides": { "sparse_infill_density": 30, "outer_wall_speed": 100 }
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

            // Resolve per-extruder filament profiles for multi-toolhead printers
            if (root.TryGetProperty("extruderFilamentProfileNames", out JsonElement extruderNamesElem)
                && extruderNamesElem.ValueKind == JsonValueKind.Array
                && extruderNamesElem.GetArrayLength() > 0)
            {
                IList<FilamentProfileDto> filaments = await profilesService.ListAvailableFilamentProfilesAsync(ct);
                var extruderProfiles = new List<FilamentProfileDto>();
                int index = 0;
                foreach (JsonElement nameElem in extruderNamesElem.EnumerateArray())
                {
                    string? name = nameElem.GetString();
                    if (string.IsNullOrEmpty(name))
                    {
                        _logger.LogError("Extruder {Index} has null/empty filament profile name — aborting multi-extruder resolution", index);
                        extruderProfiles.Clear();
                        break;
                    }

                    FilamentProfileDto? resolved = filaments.FirstOrDefault(f =>
                        string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (resolved is null)
                    {
                        _logger.LogError("Extruder {Index} filament profile '{Name}' not found — aborting multi-extruder resolution", index, name);
                        extruderProfiles.Clear();
                        break;
                    }

                    extruderProfiles.Add(resolved);
                    _logger.LogInformation("Resolved extruder {Index} filament profile: {Name}", index, resolved.Name);
                    index++;
                }

                if (extruderProfiles.Count > 0)
                {
                    profile.ExtruderFilamentProfiles = extruderProfiles;

                    // Use first extruder as the primary filament profile for backward compat
                    profile.FilamentProfile ??= extruderProfiles[0];
                }
            }

            // Apply per-slice filament colour overrides (cosmetic — affects the
            // slice preview / G-code filament_colour metadata only, not print
            // physics). Colours arrive as "#RRGGBB" strings from the frontend.
            // Profiles are resolved from a shared cache, so clone before mutating.
            ApplyFilamentColourOverrides(profile, root);

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

            // Apply user overrides — all keys are native snake_case, pass through directly
            if (profile.ProcessProfile != null && root.TryGetProperty("overrides", out JsonElement overridesElem))
            {
                int applied = 0;
                foreach (JsonProperty prop in overridesElem.EnumerateObject())
                {
                    // Store as properly-typed value, matching SerializeElementToDict format
                    profile.ProcessProfile.Settings[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                        JsonValueKind.True => "1",
                        JsonValueKind.False => "0",
                        JsonValueKind.Number => prop.Value.GetRawText(),
                        _ => (object)prop.Value.GetRawText()
                    };
                    applied++;
                }

                _logger.LogInformation("Applied {Applied} overrides to process profile", applied);
            }

            return profile;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse SlicerProfileJson: {SlicerProfileJson}", slicerProfileJson);
            return null;
        }
    }

    /// <summary>
    /// Inject per-slice filament colour overrides into the resolved profile.
    /// Multi-extruder jobs map the "filamentColours" array positionally to each
    /// extruder; single-filament jobs apply "filamentColour" to the primary.
    /// Colours are cosmetic (slice preview / G-code <c>filament_colour</c> only).
    /// OrcaSlicer expects <c>filament_colour</c> as a JSON array, so it is stored
    /// as a <see cref="List{String}"/>. Profiles come from a shared cache, so each
    /// affected entry is cloned before mutation to avoid polluting other jobs.
    /// </summary>
    internal static void ApplyFilamentColourOverrides(SlicerProfileDto profile, JsonElement root)
    {
        // Multi-extruder: positional colour per extruder index.
        if (profile.ExtruderFilamentProfiles is { Count: > 0 } extruders
            && root.TryGetProperty("filamentColours", out JsonElement coloursElem)
            && coloursElem.ValueKind == JsonValueKind.Array)
        {
            // The primary FilamentProfile may alias extruder[0] (set via `??=`). The
            // worker's single-extruder pipeline branch (used when there is exactly one
            // extruder) reads FilamentProfile, so re-point it at the coloured clone to
            // avoid dropping a positional override for that case.
            bool primaryAliasesFirstExtruder = ReferenceEquals(profile.FilamentProfile, extruders[0]);

            int i = 0;
            foreach (JsonElement colourElem in coloursElem.EnumerateArray())
            {
                if (i >= extruders.Count)
                {
                    break;
                }

                string? colour = colourElem.GetString();
                if (!string.IsNullOrWhiteSpace(colour))
                {
                    FilamentProfileDto clone = extruders[i].Clone();
                    clone.Color = colour;
                    clone.Settings["filament_colour"] = new List<string> { colour };
                    extruders[i] = clone;

                    if (i == 0 && primaryAliasesFirstExtruder)
                    {
                        profile.FilamentProfile = clone;
                    }
                }

                i++;
            }

            return;
        }

        // Single filament.
        if (profile.FilamentProfile is not null
            && root.TryGetProperty("filamentColour", out JsonElement singleElem))
        {
            string? colour = singleElem.GetString();
            if (!string.IsNullOrWhiteSpace(colour))
            {
                FilamentProfileDto clone = profile.FilamentProfile.Clone();
                clone.Color = colour;
                clone.Settings["filament_colour"] = new List<string> { colour };
                profile.FilamentProfile = clone;
            }
        }
    }

    private void TryCleanupLocalResult(Guid jobId, SlicingResult? result)
    {
        if (result?.ResultFileUrl is not { IsAbsoluteUri: true, IsFile: true } resultUri)
        {
            return;
        }

        try
        {
            string filePath = resultUri.LocalPath;
            string? directory = Path.GetDirectoryName(filePath);
            if (directory is not null &&
                string.Equals(Path.GetFileName(directory), jobId.ToString(), StringComparison.Ordinal))
            {
                Directory.Delete(directory, recursive: true);
            }
            else if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to clean local result for job {JobId}", jobId);
        }
    }
}
