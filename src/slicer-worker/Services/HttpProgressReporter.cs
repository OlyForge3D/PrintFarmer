using System.Text;
using System.Text.Json;
using Farm.Web.Shared;

namespace Farm.Slicer.Worker.Services;

/// <summary>
/// Service for reporting progress and status updates back to the API gateway via HTTP
/// </summary>
public class HttpProgressReporter : IProgressReporter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpProgressReporter> _logger;
    private readonly string _apiBaseUrl;
    private readonly string _workerId;

    public HttpProgressReporter(HttpClient httpClient, ILogger<HttpProgressReporter> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiBaseUrl = configuration["Worker:ApiBaseUrl"] ?? "http://api:5245";
        _workerId = Environment.MachineName + "-" + Environment.ProcessId;
    }

    public async Task ReportProgressAsync(Guid jobId, int progress, string message, CancellationToken cancellationToken = default)
    {
        try
        {
            var progressUpdate = new
            {
                JobId = jobId,
                WorkerId = _workerId,
                Progress = progress,
                Message = message,
                Timestamp = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(progressUpdate);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PutAsync($"{_apiBaseUrl}/api/workers/progress", content, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Reported progress for job {JobId}: {Progress}% - {Message}", jobId, progress, message);
            }
            else
            {
                _logger.LogWarning("Failed to report progress for job {JobId}: {StatusCode}", jobId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reporting progress for job {JobId}", jobId);
        }
    }

    public async Task ReportCompletionAsync(DistributedSlicingJob job, SlicingPipelineResult result, CancellationToken cancellationToken = default)
    {
        try
        {
            var completion = new
            {
                JobId = job.Id,
                WorkerId = _workerId,
                Status = SlicingJobStatus.Completed,
                ResultFileUrl = result.GcodeFileUrl,
                EstimatedPrintTimeSeconds = result.EstimatedPrintTimeSeconds,
                EstimatedFilamentUsageGrams = result.EstimatedFilamentUsageGrams,
                OutputFileSizeBytes = result.FileSizeBytes,
                LayerCount = result.LayerCount,
                CompletedAt = DateTime.UtcNow,
                Metadata = result.Metadata
            };

            var json = JsonSerializer.Serialize(completion);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_apiBaseUrl}/api/workers/complete", content, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Reported completion for job {JobId}", job.Id);
            }
            else
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
            var failure = new
            {
                JobId = jobId,
                WorkerId = _workerId,
                Status = SlicingJobStatus.Error,
                ErrorMessage = errorMessage,
                CompletedAt = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(failure);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_apiBaseUrl}/api/workers/failure", content, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Reported failure for job {JobId}: {ErrorMessage}", jobId, errorMessage);
            }
            else
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