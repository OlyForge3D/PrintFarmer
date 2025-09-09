using System.Text;
using System.Text.Json;
using Farm.Web.Shared;

namespace Farm.OrcaSlicer.Worker.Services;

public class HttpProgressReporter : IProgressReporter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpProgressReporter> _logger;
    private readonly string _apiBaseUrl;
    private readonly string _workerId;

    public HttpProgressReporter(HttpClient httpClient, ILogger<HttpProgressReporter> logger, IConfiguration configuration)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(configuration);
        _apiBaseUrl = configuration["Worker:ApiBaseUrl"] ?? "http://api:5245";
        _workerId = Environment.MachineName + "-" + Environment.ProcessId;
    }

    public async Task ReportProgressAsync(Guid jobId, int progress, string message, CancellationToken cancellationToken = default)
    {
        try
        {
            var progressUpdate = new { JobId = jobId, WorkerId = _workerId, Progress = progress, Message = message, Timestamp = DateTime.UtcNow };
            var json = JsonSerializer.Serialize(progressUpdate);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PutAsync($"{_apiBaseUrl}/api/workers/progress", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to report progress for job {JobId}: {StatusCode}", jobId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reporting progress for job {JobId}", jobId);
        }
    }

    public async Task ReportCompletionAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            var completion = new
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
            var json = JsonSerializer.Serialize(completion);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{_apiBaseUrl}/api/workers/complete", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to report completion for job {JobId}: {StatusCode}", job.Id, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reporting completion for job {JobId}", job.Id);
        }
    }

    public async Task ReportFailureAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default)
    {
        try
        {
            var failure = new { JobId = jobId, WorkerId = _workerId, Status = SlicingJobStatus.Error, ErrorMessage = errorMessage, CompletedAt = DateTime.UtcNow };
            var json = JsonSerializer.Serialize(failure);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{_apiBaseUrl}/api/workers/failure", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to report failure for job {JobId}: {StatusCode}", jobId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reporting failure for job {JobId}", jobId);
        }
    }
}
