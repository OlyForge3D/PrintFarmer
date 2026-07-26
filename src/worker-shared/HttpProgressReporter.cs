using System.Text;
using System.Text.Json;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Worker.Core;

public class HttpProgressReporter : IProgressReporter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpProgressReporter> _logger;
    private readonly string _apiBaseUrl;
    private readonly IWorkerStateService _workerState;

    public HttpProgressReporter(
        HttpClient httpClient,
        ILogger<HttpProgressReporter> logger,
        IConfiguration configuration,
        IWorkerStateService workerState)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(configuration);
        _apiBaseUrl = configuration["SlicerApi:BaseUrl"]
                   ?? configuration["Worker:ApiBaseUrl"]
                   ?? "http://api:5245";
        _workerState = workerState ?? throw new ArgumentNullException(nameof(workerState));
    }

    private static StringContent ToJsonContent(object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    /// <summary>
    /// Attaches the lease token and fencing counter this worker holds for the job.
    /// </summary>
    /// <param name="request">The outgoing worker request.</param>
    /// <param name="jobId">The claimed job.</param>
    /// <returns>
    /// <see langword="false"/> when no lease is held, in which case the caller must not send the
    /// request: an unfenced worker mutation is rejected by the API and must never be attempted.
    /// </returns>
    private bool TryAddLeaseHeaders(HttpRequestMessage request, Guid jobId)
    {
        if (!_workerState.TryGetJobLease(jobId, out WorkerJobLease lease) || lease.Token == Guid.Empty)
        {
            _logger.LogWarning("Worker request skipped because no lease is held for job {JobId}", jobId);
            return false;
        }

        request.Headers.Add(WorkerLeaseHeaders.LeaseToken, lease.Token.ToString());
        request.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            lease.Fence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    public async Task ReportProgressAsync(Guid jobId, int progress, string message, CancellationToken cancellationToken = default)
    {
        try
        {
            WorkerState workerState = _workerState.GetWorkerState();
            Guid? serviceId = workerState.RegisteredServiceId;
            if (serviceId is null || string.IsNullOrWhiteSpace(workerState.RegisteredServiceApiKey))
            {
                _logger.LogWarning("Progress report skipped because worker authentication is not ready for job {JobId}", jobId);
                return;
            }

            SliceJobProgressUpdateRequest payload = new()
            {
                ProgressPercent = progress,
                ProgressMessage = message,
            };
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                $"{_apiBaseUrl}/api/slice/{jobId}/progress")
            {
                Content = ToJsonContent(payload),
            };
            request.Headers.Add(WorkerLeaseHeaders.WorkerKey, workerState.RegisteredServiceApiKey);
            request.Headers.Add(WorkerLeaseHeaders.WorkerId, serviceId.Value.ToString());
            if (!TryAddLeaseHeaders(request, jobId))
            {
                return;
            }

            HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Progress report failed {JobId} status {StatusCode}", jobId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reporting progress {JobId}", jobId);
        }
    }

    public async Task ReportCompletionAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);
        await Task.CompletedTask;
        _logger.LogDebug(
            "Completion for job {JobId} is reported by the authenticated artifact workflow",
            job.Id);
    }

    public async Task ReportFailureAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default)
    {
        _ = errorMessage;
        try
        {
            WorkerState workerState = _workerState.GetWorkerState();
            Guid? serviceId = workerState.RegisteredServiceId;
            if (serviceId is null || string.IsNullOrWhiteSpace(workerState.RegisteredServiceApiKey))
            {
                _logger.LogWarning("Failure report skipped because worker authentication is not ready for job {JobId}", jobId);
                return;
            }

            using HttpRequestMessage request = new(
                HttpMethod.Post,
                $"{_apiBaseUrl}/api/slice/{jobId}/fail")
            {
                Content = ToJsonContent(new FailSliceJobRequest("Slicing worker could not complete the job.")),
            };
            request.Headers.Add(WorkerLeaseHeaders.WorkerKey, workerState.RegisteredServiceApiKey);
            request.Headers.Add(WorkerLeaseHeaders.WorkerId, serviceId.Value.ToString());
            if (!TryAddLeaseHeaders(request, jobId))
            {
                return;
            }

            HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failure report failed {JobId} status {StatusCode}", jobId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reporting failure {JobId}", jobId);
        }
    }
}
