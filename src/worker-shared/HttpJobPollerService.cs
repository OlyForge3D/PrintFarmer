using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

    internal static Uri ResolveModelFileUri(Uri apiBaseAddress, string modelFileUrl)
    {
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFileUrl);

        return new Uri(apiBaseAddress, modelFileUrl);
    }

    /// <summary>
    /// Marker file that claims ownership of a job's local work after an ambiguous outcome.
    /// </summary>
    private const string RecoveryMarkerFileName = ".printfarmer-recovery.json";
    private const long DefaultRecoveryMaxBytes = 1L * 1024 * 1024 * 1024;
    private const double DefaultRecoveryMinimumAgeHours = 24;

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

                using HttpClient httpClient =
                    _httpClientFactory.CreateClient(WorkerApiHttpClient.Name);
                httpClient.BaseAddress = new Uri(apiBaseUrl);

                // A real slice routinely exceeds 30s; the request timeout must exceed the slice
                // duration or the worker abandons work the API still considers leased.
                httpClient.Timeout = TimeSpan.FromSeconds(
                    int.TryParse(_configuration["Worker:HttpTimeoutSeconds"], out int timeoutSeconds) && timeoutSeconds > 0
                        ? timeoutSeconds
                        : 600);

                // Only the claim is allowed to rely on default headers: it is the one worker request
                // that has no job and therefore no lease. Every job mutation builds its own headers
                // explicitly (see CreateJobMutationRequest), and a job lease is never stored here.
                httpClient.DefaultRequestHeaders.Add(WorkerLeaseHeaders.WorkerKey, currentWorkerState.RegisteredServiceApiKey);
                httpClient.DefaultRequestHeaders.Add(WorkerLeaseHeaders.WorkerId, registeredServiceId.Value.ToString());

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
                // The engine arrives as a validated canonical value; nothing is cast or guessed here.
                DistributedSlicingJob job = new DistributedSlicingJob
                {
                    Id = jobStatus.Id,
                    ClaimToken = jobStatus.ClaimToken,
                    WorkerId = registeredServiceId.Value.ToString(),
                    ModelFileUrl = ResolveModelFileUri(httpClient.BaseAddress, jobStatus.ModelFileUrl),
                    ModelFileName = jobStatus.ModelFileName,
                    ModelSha256 = jobStatus.ModelSha256,
                    EngineType = jobStatus.SlicerEngine,
                    SlicerEngine = jobStatus.SlicerEngine.ToString(),
                    SlicerProfileJson = jobStatus.SlicerProfileJson,
                    Status = SlicingJobStatus.Slicing, // in-progress mapping
                    StartedAt = DateTime.UtcNow,
                    ModelTransformJson = jobStatus.ModelTransformJson,
                    ModelFileUrls = jobStatus.ModelFileUrls,
                    ModelFileTransforms = jobStatus.ModelFileTransforms,
                    LeaseToken = jobStatus.LeaseToken,
                    LeaseFence = jobStatus.LeaseFence,
                    NativeProfiles = NativeSlicerProfiles.FromJob(
                        jobStatus.MachineProfileJson,
                        jobStatus.ProcessProfileJson,
                        jobStatus.FilamentProfileJson,
                        jobStatus.MachineProfileSha256,
                        jobStatus.ProcessProfileSha256,
                        jobStatus.FilamentProfileSha256),
                    MachineProfileSha256 = jobStatus.MachineProfileSha256,
                    ProcessProfileSha256 = jobStatus.ProcessProfileSha256,
                    FilamentProfileSha256 = jobStatus.FilamentProfileSha256,
                };

                // Resolve profile names from SlicerProfileJson into full SlicerProfileDto
                job.Profile = await ResolveProfileFromJsonAsync(job.SlicerProfileJson, stoppingToken);

                // The lease this job was claimed under is registered inside HandleJobAsync and
                // released there, so it is never ambient on the client's default headers.
                _workerState.IncrementActiveJobs();
                _logger.LogInformation("Claimed job {JobId}, starting processing", job.Id);

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

    /// <summary>
    /// Runs the full lifecycle of one claimed job: lease renewal, pipeline execution, artifact
    /// upload and terminal reporting.
    /// </summary>
    /// <remarks>
    /// The lease this job was claimed under is registered here and released in the finally block,
    /// so every mutation issued for the job — including the ones the background renewal loop sends —
    /// resolves the same lease from a single source of truth.
    /// </remarks>
    private async Task HandleJobAsync(DistributedSlicingJob job, HttpClient httpClient, CancellationToken ct)
    {
        DateTime start = DateTime.UtcNow;
        SlicingResult? result = null;
        bool terminalAcknowledgement = false;

        // Written by the renewal loop, read after the loop has been awaited or after the linked
        // token has been observed as cancelled; Volatile/Interlocked keeps the hand-off explicit.
        int leaseLost = 0;
        Task? renewalLoop = null;

        _workerState.SetJobLease(
            job.Id,
            new WorkerJobLease(job.LeaseToken, job.LeaseFence, job.ClaimToken));

        using CancellationTokenSource jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken jobToken = jobCts.Token;

        try
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            TryCleanupConfiguredRecoveryDirectories(job.Id);

            // Emit initial progress (0%)
            await TrySendProgressAsync(
                httpClient,
                job.Id,
                job.ClaimToken,
                0,
                "Starting slicing",
                jobToken);

            // Start a lease-renewal loop to prevent the API from reclaiming the job while we're actively processing.
            try
            {
                int leaseDurationSeconds = int.Parse(_configuration["Worker:LeaseDurationSeconds"] ?? "300");
                int renewIntervalSeconds = Math.Max(10, leaseDurationSeconds / 3);

                renewalLoop = Task.Run(
                    async () =>
                {
                    while (!jobToken.IsCancellationRequested)
                    {
                        try
                        {
                            LeaseRenewalOutcome outcome =
                                await RenewLeaseOnceAsync(httpClient, job, leaseDurationSeconds, jobToken);
                            if (outcome == LeaseRenewalOutcome.Lost)
                            {
                                // The lease is gone. Continuing to slice would burn compute and could
                                // publish an artifact under a fencing token the API has already
                                // superseded, so stop the job's work now.
                                _ = Interlocked.Exchange(ref leaseLost, 1);
                                await jobCts.CancelAsync();
                                break;
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

                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(renewIntervalSeconds), jobToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to start lease renew loop for job {JobId}", job.Id);
            }

            // Execute the slicing pipeline (downloads STL, runs slicer, generates G-code)
            result = await ExecutePipelineAsync(job, scope.ServiceProvider, jobToken);
            if (!result.Success)
            {
                throw new InvalidOperationException("Slicing pipeline reported failure");
            }

            // A lease lost while the pipeline was finishing must never be followed by an upload:
            // the fence the API holds is newer than the one this artifact would be published under.
            jobToken.ThrowIfCancellationRequested();

            // Mid-progress update (pipeline finished but artifacts pending)
            // Use heuristic progress since SlicingResult doesn't expose granular percentage yet.
            await TrySendProgressAsync(
                httpClient,
                job.Id,
                job.ClaimToken,
                85,
                "Slicing complete, uploading artifacts",
                jobToken);

            _logger.LogInformation("Job {JobId} slicing completed in {TotalSeconds:F1}s", job.Id, (DateTime.UtcNow - start).TotalSeconds);

            // Upload artifacts (G-code file and any metadata) with declared digests
            List<Guid> artifactIds = await UploadArtifactsAsync(job, result, httpClient, jobToken);

            // Complete the job with artifact references and the profile digests actually written
            CompleteSliceJobRequest completeRequest = new CompleteSliceJobRequest
            {
                PrimaryArtifactId = artifactIds[0],
                AdditionalArtifactIds = artifactIds.Skip(1).ToArray(),
                EstimatedPrintTimeSeconds = (int?)Math.Round(result.EstimatedPrintTimeSeconds),
                FilamentUsedGrams = (decimal?)Math.Round(result.EstimatedFilamentUsageGrams, 2),
                LogText = result.Metadata.TryGetValue("SlicerLog", out string? logObj) ? logObj?.ToString() : null,
                MachineProfileSha256 = job.MachineProfileSha256 ?? job.NativeProfiles?.MachineSha256,
                ProcessProfileSha256 = job.ProcessProfileSha256 ?? job.NativeProfiles?.ProcessSha256,
                FilamentProfileSha256 = job.FilamentProfileSha256 ?? job.NativeProfiles?.FilamentSha256,
            };

            using HttpRequestMessage completeMessage = CreateJobMutationRequest(
                httpClient,
                HttpMethod.Post,
                job.Id,
                $"/api/slice/{job.Id}/complete",
                JsonContent.Create(completeRequest));
            using HttpResponseMessage completeResponse = await httpClient.SendAsync(completeMessage, jobToken);

            if (!completeResponse.IsSuccessStatusCode)
            {
                string errorContent = await completeResponse.Content.ReadAsStringAsync(jobToken);
                throw new InvalidOperationException($"Failed to complete job: {completeResponse.StatusCode} - {errorContent}");
            }

            // Terminal API acknowledgement received: the local work is now safe to discard.
            terminalAcknowledgement = true;
            _logger.LogInformation("Job {JobId} completed successfully with {ArtifactIdsCount} artifacts", job.Id, artifactIds.Count);
        }
        catch (OperationCanceledException ex)
        {
            if (Volatile.Read(ref leaseLost) == 1)
            {
                // The API no longer accepts this worker's lease for this job. Nothing further may be
                // reported under it, so the failure is recorded durably on disk and the API reclaims
                // the job when the lease expires.
                _logger.LogError(
                    "Job {JobId} lost its lease; slicing was cancelled so no artifact is published under a stale fencing token",
                    job.Id);
                TryWriteRecoveryMarker(job.Id, result, "lease_lost");
            }
            else if (ct.IsCancellationRequested)
            {
                _logger.LogWarning("Job {JobId} cancelled during worker shutdown", job.Id);
                TryWriteRecoveryMarker(job.Id, result, "cancelled");
            }
            else
            {
                _logger.LogError(ex, "Job {JobId} timed out: {Message}", job.Id, ex.Message);
                terminalAcknowledgement = await TryReportFailureAsync(
                    httpClient,
                    job.Id,
                    job.ClaimToken,
                    $"Artifact upload or job completion timed out: {ex.Message}",
                    ct);
                if (!terminalAcknowledgement)
                {
                    TryWriteRecoveryMarker(job.Id, result, "timeout");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed: {Message}", job.Id, ex.Message);

            if (Volatile.Read(ref leaseLost) == 1)
            {
                // Reporting the failure would be denied: the lease this worker holds is no longer
                // the one the API recognises. Fail closed rather than issue an unauthorised request.
                _logger.LogError(
                    "Job {JobId} failure could not be reported because its lease is no longer current; the API will reclaim it on lease expiry",
                    job.Id);
            }
            else
            {
                // Report failure to the API so the job doesn't sit in Processing until lease expires
                terminalAcknowledgement =
                    await TryReportFailureAsync(httpClient, job.Id, job.ClaimToken, ex.Message, ct);
            }

            if (!terminalAcknowledgement)
            {
                // The outcome is ambiguous from the worker's point of view, so the local work is kept
                // and marked for recovery instead of being deleted.
                TryWriteRecoveryMarker(
                    job.Id,
                    result,
                    Volatile.Read(ref leaseLost) == 1 ? "lease_lost" : ex.GetType().Name);
            }
        }
        finally
        {
            // Stop the lease renewal loop and wait for it so nothing outlives the job.
            try
            {
                await jobCts.CancelAsync();
                if (renewalLoop is not null)
                {
                    await renewalLoop;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: the renewal loop observes the job's cancellation.
            }
            catch (ObjectDisposedException exception)
            {
                _logger.LogDebug(exception, "Lease renewal token source for job {JobId} was already disposed", job.Id);
            }

            if (terminalAcknowledgement)
            {
                TryCleanupLocalResult(job.Id, result);
            }

            _workerState.ClearJobWorkDirectory(job.Id);
            _workerState.ClearJobLease(job.Id);
            _workerState.DecrementActiveJobs();
        }
    }

    /// <summary>
    /// Outcome of a single lease renewal attempt.
    /// </summary>
    private enum LeaseRenewalOutcome
    {
        /// <summary>The API extended the lease.</summary>
        Renewed,

        /// <summary>The attempt failed for a reason that may resolve on the next attempt.</summary>
        Transient,

        /// <summary>The API refuses this worker's lease for this job; the job must stop.</summary>
        Lost,
    }

    /// <summary>
    /// Builds a request carrying exactly one value for each worker claim and lease header
    /// <c>AuthorizeWorkerMutationAsync</c> requires.
    /// </summary>
    /// <param name="httpClient">The client the request will be sent on; its default headers are consulted so a value is never presented twice.</param>
    /// <param name="method">HTTP method for the mutation.</param>
    /// <param name="jobId">The claimed job being mutated.</param>
    /// <param name="requestUri">Request URI relative to the client's base address.</param>
    /// <param name="content">Request body, when the mutation has one.</param>
    /// <returns>A request that is safe to send exactly once.</returns>
    /// <exception cref="InvalidOperationException">
    /// The worker is not registered, or holds no lease for <paramref name="jobId"/>. The path fails
    /// closed rather than emitting an unauthenticated or unfenced request.
    /// </exception>
    private HttpRequestMessage CreateJobMutationRequest(
        HttpClient httpClient,
        HttpMethod method,
        Guid jobId,
        string requestUri,
        HttpContent? content)
    {
        WorkerState state = _workerState.GetWorkerState();
        if (state.RegisteredServiceId is not { } serviceId || string.IsNullOrWhiteSpace(state.RegisteredServiceApiKey))
        {
            throw new InvalidOperationException(
                $"Worker registration is unavailable; refusing to send an unauthenticated mutation for job {jobId}.");
        }

        if (!_workerState.TryGetJobLease(jobId, out WorkerJobLease lease) ||
            lease.Token == Guid.Empty ||
            lease.ClaimToken == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"No lease is held for job {jobId}; refusing to send an unfenced mutation.");
        }

        HttpRequestMessage request = new(method, requestUri);
        try
        {
            request.Content = content;
            SetSingleHeaderValue(request, httpClient, WorkerLeaseHeaders.WorkerKey, state.RegisteredServiceApiKey!);
            SetSingleHeaderValue(request, httpClient, WorkerLeaseHeaders.WorkerId, serviceId.ToString());
            SetSingleHeaderValue(request, httpClient, WorkerClaimHeaders.ClaimToken, lease.ClaimToken.ToString());
            SetSingleHeaderValue(request, httpClient, WorkerLeaseHeaders.LeaseToken, lease.Token.ToString());
            SetSingleHeaderValue(
                request,
                httpClient,
                WorkerLeaseHeaders.LeaseFence,
                lease.Fence.ToString(CultureInfo.InvariantCulture));
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Guarantees the wire carries this header exactly once with the given value.
    /// </summary>
    /// <remarks>
    /// A value already inherited from <see cref="HttpClient.DefaultRequestHeaders"/> is left alone
    /// when it is a single value that already matches; otherwise the request carries the value
    /// itself, which takes precedence over the client's defaults. Either way the receiving API sees
    /// one unambiguous value rather than a two-element <c>StringValues</c>.
    /// </remarks>
    /// <param name="request">The outgoing request.</param>
    /// <param name="httpClient">The client whose default headers may already supply the value.</param>
    /// <param name="name">The header name.</param>
    /// <param name="value">The single value the API must observe.</param>
    private static void SetSingleHeaderValue(HttpRequestMessage request, HttpClient httpClient, string name, string value)
    {
        _ = request.Headers.Remove(name);

        if (httpClient.DefaultRequestHeaders.TryGetValues(name, out IEnumerable<string>? defaults))
        {
            string[] defaultValues = defaults as string[] ?? defaults.ToArray();
            if (defaultValues.Length == 1 && string.Equals(defaultValues[0], value, StringComparison.Ordinal))
            {
                // Inherited exactly once with the correct value: adding it again is what produced
                // ambiguous authentication headers in the first place.
                return;
            }
        }

        request.Headers.Add(name, value);
    }

    /// <summary>
    /// Sends a single lease renewal request, explicitly carrying the worker identity and the
    /// exact lease token and fencing counter this job was claimed under.
    /// </summary>
    /// <remarks>
    /// The renewal must not depend on the shared <see cref="HttpClient"/>'s default headers: the
    /// loop runs concurrently with the poll loop for the lifetime of a potentially long-running
    /// slice, and a job lease is never ambient state. Building the request explicitly keeps renewal
    /// self-contained and duplicate-free.
    /// </remarks>
    /// <param name="httpClient">Client bound to the API base address.</param>
    /// <param name="job">The claimed job whose lease is being renewed.</param>
    /// <param name="leaseDurationSeconds">Requested lease extension.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether the lease was renewed, transiently failed, or is gone for good.</returns>
    private async Task<LeaseRenewalOutcome> RenewLeaseOnceAsync(
        HttpClient httpClient,
        DistributedSlicingJob job,
        int leaseDurationSeconds,
        CancellationToken ct)
    {
        RenewLeaseRequest renewReq = new RenewLeaseRequest { LeaseDurationSeconds = leaseDurationSeconds };
        using HttpRequestMessage renewRequest = CreateJobMutationRequest(
            httpClient,
            HttpMethod.Post,
            job.Id,
            $"/api/slice/{job.Id}/renew-lease",
            JsonContent.Create(renewReq));

        using HttpResponseMessage resp = await httpClient.SendAsync(renewRequest, ct);
        if (resp.IsSuccessStatusCode)
        {
            return LeaseRenewalOutcome.Renewed;
        }

        switch (resp.StatusCode)
        {
            case HttpStatusCode.Conflict:
            case HttpStatusCode.Forbidden:
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.NotFound:
                // The API no longer recognises this worker's claim on the job: the lease expired,
                // was reassigned, superseded by a newer fence, or the job itself is gone. Retrying
                // cannot recover it, so this is terminal for the job rather than transient.
                _logger.LogWarning(
                    "Lease renew for job {JobId} was rejected as a conflict ({RespStatusCode}); this worker's lease or fencing token is no longer current",
                    job.Id,
                    resp.StatusCode);
                return LeaseRenewalOutcome.Lost;
            default:
                _logger.LogDebug("Lease renew for job {JobId} returned {RespStatusCode}", job.Id, resp.StatusCode);
                return LeaseRenewalOutcome.Transient;
        }
    }

    private async Task TrySendProgressAsync(
        HttpClient client,
        Guid jobId,
        Guid claimToken,
        int percent,
        string message,
        CancellationToken ct)
    {
        try
        {
            EnsureCurrentClaim(jobId, claimToken);
            SliceJobProgressUpdateRequest progressReq = new SliceJobProgressUpdateRequest
            {
                ProgressPercent = percent,
                ProgressMessage = message
            };
            using HttpRequestMessage request = CreateJobMutationRequest(
                client,
                HttpMethod.Post,
                jobId,
                $"/api/slice/{jobId}/progress",
                JsonContent.Create(progressReq));
            using HttpResponseMessage resp = await client.SendAsync(request, ct);
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

    private async Task<bool> TryReportFailureAsync(
        HttpClient client,
        Guid jobId,
        Guid claimToken,
        string errorMessage,
        CancellationToken ct)
    {
        try
        {
            EnsureCurrentClaim(jobId, claimToken);
            string truncated = errorMessage.Length > 1000 ? errorMessage[..1000] : errorMessage;

            using HttpRequestMessage request = CreateJobMutationRequest(
                client,
                HttpMethod.Post,
                jobId,
                $"/api/slice/{jobId}/fail",
                JsonContent.Create(new FailSliceJobRequest(truncated)));
            using HttpResponseMessage resp = await client.SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Fail report for job {JobId} returned {RespStatusCode}; local work will be retained for recovery",
                    jobId,
                    resp.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to report failure for job {JobId}; local work will be retained for recovery",
                jobId);
            return false;
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
            if (result.ResultFileUrl.IsAbsoluteUri && result.ResultFileUrl.IsFile)
            {
                gcodeFilePath = NormalizeLocalPath(result.ResultFileUrl);
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

        _logger.LogInformation("Uploading G-code artifact for job {JobId}", job.Id);

        // Upload the primary G-code file without buffering the full artifact in memory, with a
        // declared digest so the API can refuse bytes that differ from what this worker produced.
        await using FileStream gcodeStream = OpenArtifactFileStream(gcodeFilePath);
        string declaredSha256 = Convert.ToHexString(await SHA256.HashDataAsync(gcodeStream, ct));
        gcodeStream.Position = 0;

        using MultipartFormDataContent gcodeContent = new MultipartFormDataContent();
        StreamContent gcodeFileContent = new StreamContent(gcodeStream);
        gcodeFileContent.Headers.ContentLength = gcodeStream.Length;
        gcodeFileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/x.gcode");
        gcodeContent.Add(gcodeFileContent, "file", Path.GetFileName(gcodeFilePath));
        gcodeContent.Add(new StringContent("gcode"), "kind");
        gcodeContent.Add(new StringContent(declaredSha256), "sha256");
        gcodeContent.Add(
            new StringContent(gcodeStream.Length.ToString(CultureInfo.InvariantCulture)),
            "sizeBytes");

        EnsureCurrentClaim(job.Id, job.ClaimToken);
        using HttpRequestMessage uploadRequest = CreateJobMutationRequest(
            httpClient,
            HttpMethod.Post,
            job.Id,
            $"/api/slice/{job.Id}/artifacts",
            gcodeContent);
        using HttpResponseMessage uploadResponse = await httpClient.SendAsync(uploadRequest, ct);

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

        return artifactIds;
    }

    private void EnsureCurrentClaim(Guid jobId, Guid claimToken)
    {
        if (!_workerState.TryGetJobLease(jobId, out WorkerJobLease lease) ||
            lease.ClaimToken == Guid.Empty ||
            lease.ClaimToken != claimToken)
        {
            throw new InvalidOperationException(
                $"The claim token for job {jobId} is no longer current.");
        }
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
                        _ => prop.Value.GetRawText()
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

    /// <summary>
    /// Records a durable recovery marker beside a job's output so an ambiguous outcome keeps its
    /// local work instead of destroying it.
    /// </summary>
    /// <param name="jobId">The job whose outcome is unknown.</param>
    /// <param name="result">The pipeline result, when the pipeline produced one.</param>
    /// <param name="reason">Non-sensitive failure category.</param>
    private void TryWriteRecoveryMarker(Guid jobId, SlicingResult? result, string reason)
    {
        if (!TryResolveJobDirectory(jobId, result, out string directory))
        {
            return;
        }

        try
        {
            string marker = Path.Join(directory, RecoveryMarkerFileName);
            string payload = JsonSerializer.Serialize(new
            {
                jobId,
                reason,
                recordedAtUtc = DateTime.UtcNow,
                state = "upload_or_completion_unconfirmed",
            });
            File.WriteAllText(marker, payload);
            _logger.LogWarning(
                "Preserved recoverable local work for job {JobId} ({Reason})",
                jobId,
                reason);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Failed to write recovery marker for job {JobId}", jobId);
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Failed to write recovery marker for job {JobId}", jobId);
        }
    }

    private void TryCleanupLocalResult(Guid jobId, SlicingResult? result)
    {
        if (!TryResolveJobDirectory(jobId, result, out string directory))
        {
            if (result?.ResultFileUrl is { IsAbsoluteUri: true, IsFile: true } fileUri &&
                File.Exists(NormalizeLocalPath(fileUri)))
            {
                TryDeleteFile(jobId, NormalizeLocalPath(fileUri));
            }

            return;
        }

        try
        {
            string? configuredWorkingDirectory = _configuration["Worker:WorkingDirectory"];
            if (string.IsNullOrWhiteSpace(configuredWorkingDirectory) ||
                !_workerState.TryGetJobWorkDirectory(jobId, out string recordedAttemptDirectory) ||
                !IsPathWithinRoot(directory, configuredWorkingDirectory) ||
                !string.Equals(
                    Path.GetFullPath(directory),
                    Path.GetFullPath(recordedAttemptDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Refused local cleanup for job {JobId}; result directory was not the recorded configured attempt",
                    jobId);
                return;
            }

            Directory.Delete(directory, recursive: true);
            TryDeleteEmptyJobParent(directory, jobId);
            TryCleanupRecoveryDirectories(configuredWorkingDirectory, jobId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to clean local result for job {JobId}", jobId);
        }
    }

    private void TryDeleteFile(Guid jobId, string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to clean local result for job {JobId}", jobId);
        }
    }

    private void TryDeleteEmptyJobParent(string attemptDirectory, Guid jobId)
    {
        string? jobDirectory = Path.GetDirectoryName(attemptDirectory);
        if (string.IsNullOrEmpty(jobDirectory) ||
            !string.Equals(Path.GetFileName(jobDirectory), jobId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(jobDirectory).Any())
            {
                Directory.Delete(jobDirectory);
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Retained job work parent {JobDirectory} because it was not empty or unavailable", jobDirectory);
        }
    }

    private void TryCleanupRecoveryDirectories(string workingDirectory, Guid activeJobId)
    {
        int leaseDurationSeconds = int.TryParse(
            _configuration["Worker:LeaseDurationSeconds"],
            out int configuredLeaseDurationSeconds)
            ? Math.Max(configuredLeaseDurationSeconds, 1)
            : 300;
        double configuredMinimumAgeHours = double.TryParse(
            _configuration["Worker:RecoveryMinimumAgeHours"],
            out double parsedMinimumAgeHours)
            ? Math.Max(parsedMinimumAgeHours, 0)
            : DefaultRecoveryMinimumAgeHours;
        TimeSpan minimumAge = TimeSpan.FromSeconds(
            Math.Max(
                configuredMinimumAgeHours * 3600,
                leaseDurationSeconds + 3600));
        long maxBytes = long.TryParse(
            _configuration["Worker:RecoveryMaxBytes"],
            out long configuredMaxBytes)
            ? Math.Max(configuredMaxBytes, 0)
            : DefaultRecoveryMaxBytes;

        IReadOnlyList<string> deleted = CleanupRecoveryDirectories(
            workingDirectory,
            DateTime.UtcNow,
            minimumAge,
            maxBytes,
            activeJobId,
            _workerState.GetActiveJobWorkDirectories());
        if (deleted.Count > 0)
        {
            _logger.LogWarning(
                "Removed {RecoveryDirectoryCount} expired recovery directories under {WorkingDirectory} to enforce the {RecoveryMaxBytes} byte recovery quota",
                deleted.Count,
                workingDirectory,
                maxBytes);
        }
    }

    private void TryCleanupConfiguredRecoveryDirectories(Guid activeJobId)
    {
        string? workingDirectory = _configuration["Worker:WorkingDirectory"];
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            try
            {
                TryCleanupRecoveryDirectories(workingDirectory, activeJobId);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Recovery cleanup was skipped for {WorkingDirectory}; job lifecycle will continue",
                    workingDirectory);
            }
        }
    }

    /// <summary>
    /// Removes only old, marked recovery attempts when the bounded recovery quota is exceeded.
    /// Recent attempts remain untouched because their remote lease may still be valid.
    /// </summary>
    internal static IReadOnlyList<string> CleanupRecoveryDirectories(
        string workingDirectory,
        DateTime utcNow,
        TimeSpan minimumAge,
        long maxBytes,
        Guid? activeJobId = null,
        IReadOnlyCollection<string>? activeAttemptDirectories = null)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory) || maxBytes < 0)
        {
            return [];
        }

        List<(string Path, Guid JobId, DateTime LastWriteUtc, long Size)> recoveryDirectories = [];
        try
        {
            foreach (string jobDirectory in Directory.EnumerateDirectories(workingDirectory))
            {
                if (!Guid.TryParse(Path.GetFileName(jobDirectory), out Guid jobId))
                {
                    continue;
                }

                try
                {
                    IEnumerable<string> candidates = Directory.EnumerateDirectories(jobDirectory)
                        .Where(directory =>
                            Guid.TryParse(Path.GetFileName(directory), out _)
                            || File.Exists(Path.Join(directory, RecoveryMarkerFileName)));
                    if (File.Exists(Path.Join(jobDirectory, RecoveryMarkerFileName)))
                    {
                        candidates = candidates.Append(jobDirectory);
                    }

                    foreach (string candidate in candidates)
                    {
                        string markerPath = Path.Join(candidate, RecoveryMarkerFileName);
                        DateTime lastWriteUtc = File.Exists(markerPath)
                            ? File.GetLastWriteTimeUtc(markerPath)
                            : Directory.GetLastWriteTimeUtc(candidate);
                        long size = Directory.EnumerateFiles(candidate, "*", SearchOption.AllDirectories)
                            .Sum(file => new FileInfo(file).Length);
                        recoveryDirectories.Add((candidate, jobId, lastWriteUtc, size));
                    }
                }
                catch (Exception)
                {
                    // Treat inaccessible or concurrently changing recovery data as active and retain it.
                }
            }
        }
        catch (Exception)
        {
            // Treat an inaccessible recovery root as active and retain all local work.
            return [];
        }

        long totalBytes = recoveryDirectories.Sum(directory => directory.Size);
        if (totalBytes <= maxBytes)
        {
            return [];
        }

        List<string> deleted = [];
        foreach ((string path, Guid jobId, DateTime lastWriteUtc, long size) in recoveryDirectories
            .OrderBy(directory => directory.LastWriteUtc))
        {
            if (totalBytes <= maxBytes ||
                (activeJobId.HasValue && jobId == activeJobId.Value) ||
                IsActiveAttemptOrAncestor(path, activeAttemptDirectories) ||
                utcNow - lastWriteUtc < minimumAge)
            {
                continue;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                totalBytes -= size;
                deleted.Add(path);
            }
            catch (Exception)
            {
                // A concurrent worker may own or remove this directory; leave it in place.
            }
        }

        return deleted;
    }

    private static bool IsActiveAttemptOrAncestor(
        string path,
        IReadOnlyCollection<string>? activeAttemptDirectories)
    {
        if (activeAttemptDirectories is null)
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path);
        return activeAttemptDirectories.Any(activePath =>
        {
            string activeFullPath = Path.GetFullPath(activePath);
            string relativePath = Path.GetRelativePath(fullPath, activeFullPath);
            return relativePath == "." ||
                (!Path.IsPathRooted(relativePath) &&
                 !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                 !string.Equals(relativePath, "..", StringComparison.Ordinal));
        });
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string relativePath = Path.GetRelativePath(fullRoot, fullPath);
        return relativePath == "." ||
            (!Path.IsPathRooted(relativePath) &&
             !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
             !string.Equals(relativePath, "..", StringComparison.Ordinal));
    }

    /// <param name="jobId">The job identifier, which names the parent working directory.</param>
    /// <param name="result">The pipeline result, when the pipeline produced one.</param>
    /// <param name="directory">The resolved directory when the method returns true.</param>
    /// <returns><see langword="true"/> when a job-owned directory was resolved.</returns>
    private static bool TryResolveJobDirectory(Guid jobId, SlicingResult? result, out string directory)
    {
        directory = string.Empty;
        if (result?.ResultFileUrl is not { IsAbsoluteUri: true, IsFile: true } resultUri)
        {
            return false;
        }

        string? candidate = Path.GetDirectoryName(NormalizeLocalPath(resultUri));
        while (!string.IsNullOrEmpty(candidate))
        {
            string? parent = Path.GetDirectoryName(candidate);
            if (!string.IsNullOrEmpty(parent) &&
                string.Equals(Path.GetFileName(parent), jobId.ToString(), StringComparison.Ordinal))
            {
                // Current workers isolate attempts under {jobId}/{claimToken}. Legacy workers wrote
                // directly under {jobId}/output, so retain compatibility when cleaning old results.
                directory = Guid.TryParse(Path.GetFileName(candidate), out _)
                    ? candidate
                    : parent;
                return Directory.Exists(directory);
            }

            candidate = parent;
        }

        return false;
    }

    /// <summary>
    /// Converts a <c>file:</c> URI to a usable local path.
    /// </summary>
    /// <param name="uri">The absolute file URI produced by a pipeline.</param>
    /// <returns>
    /// The local path. A <c>localhost</c> authority is stripped because it makes
    /// <see cref="Uri.LocalPath"/> return a UNC-style path that no local API can open on Windows.
    /// </returns>
    private static string NormalizeLocalPath(Uri uri)
    {
        string localPath = uri.LocalPath;
        const string LocalHostPrefix = @"\\localhost\";
        return localPath.StartsWith(LocalHostPrefix, StringComparison.OrdinalIgnoreCase)
            ? localPath[LocalHostPrefix.Length..]
            : localPath;
    }
}
