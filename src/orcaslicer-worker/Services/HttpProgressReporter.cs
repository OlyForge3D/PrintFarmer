using Farm.Web.Shared;

namespace Farm.OrcaSlicer.Worker.Services;

public class HttpProgressReporter : IProgressReporter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpProgressReporter> _logger;
    private readonly IConfiguration _configuration;

    public HttpProgressReporter(HttpClient httpClient, ILogger<HttpProgressReporter> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
    }

    private string ApiBase => _configuration["Worker:StorageEndpoint"] ?? "http://api:5245";

    public Task ReportProgressAsync(Guid jobId, int progress, string message, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Job {JobId} progress {Progress}%: {Message}", jobId, progress, message);
        return Task.CompletedTask; // Phase 1: In-memory only
    }

    public Task ReportCompletionAsync(DistributedSlicingJob job, SlicingPipelineResult result, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Job {JobId} complete -> {Url}", job.Id, result.GcodeFileUrl);
        return Task.CompletedTask; // Phase 1 stub
    }

    public Task ReportFailureAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default)
    {
        _logger.LogError("Job {JobId} failed: {Error}", jobId, errorMessage);
        return Task.CompletedTask; // Phase 1 stub
    }
}
