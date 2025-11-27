using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;
using Microsoft.Extensions.Configuration;

namespace Farm.Slicer.Worker.Core;

public class HttpProgressReporter : IProgressReporter
{
    private readonly HttpClient _httpClient;
    private readonly IUnifiedLoggingService _logger;
    private readonly string _apiBaseUrl;
    private readonly string _workerId;

    public HttpProgressReporter(HttpClient httpClient, IUnifiedLoggingService logger, IConfiguration configuration)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(configuration);
        _apiBaseUrl = configuration["Worker:ApiBaseUrl"] ?? "http://api:5245";
        _workerId = WorkerIdentity.Create();
    }

    private static StringContent ToJsonContent(object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    public async Task ReportProgressAsync(Guid jobId, int progress, string message, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new { JobId = jobId, WorkerId = _workerId, Progress = progress, Message = message, Timestamp = DateTime.UtcNow };
            HttpResponseMessage response = await _httpClient.PutAsync($"{_apiBaseUrl}/api/workers/progress", ToJsonContent(payload), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"Progress report failed {jobId} status {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error reporting progress {jobId}");
        }
    }

    public async Task ReportCompletionAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            var payload = new
            {
                JobId = job.Id,
                WorkerId = _workerId,
                Status = SlicingJobStatus.Completed,
                ResultFileUrl = result.ResultFileUrl,
                EstimatedPrintTimeSeconds = result.EstimatedPrintTimeSeconds,
                EstimatedFilamentUsageGrams = result.EstimatedFilamentUsageGrams,
                OutputFileSizeBytes = result.OutputFileSizeBytes,
                LayerCount = result.LayerCount,
                CompletedAt = DateTime.UtcNow,
                Metadata = result.Metadata
            };
            HttpResponseMessage response = await _httpClient.PostAsync($"{_apiBaseUrl}/api/workers/complete", ToJsonContent(payload), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"Completion report failed {job.Id} status {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error reporting completion {job.Id}");
        }
    }

    public async Task ReportFailureAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new { JobId = jobId, WorkerId = _workerId, Status = SlicingJobStatus.Error, ErrorMessage = errorMessage, CompletedAt = DateTime.UtcNow };
            HttpResponseMessage response = await _httpClient.PostAsync($"{_apiBaseUrl}/api/workers/failure", ToJsonContent(payload), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"Failure report failed {jobId} status {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error reporting failure {jobId}");
        }
    }
}
