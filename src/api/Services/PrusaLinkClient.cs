using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services;

// Simple adapter to convert ILogger<T> to ILogger<U>
internal class LoggerAdapter<T>(ILogger logger) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => logger.Log(logLevel, eventId, state, exception, formatter);
}

public class PrusaLinkClient(HttpClient http, ILogger<PrusaLinkClient>? logger = null) : PrinterClientBase, IPrusaLinkClient
{
    private readonly PrusaLinkApiClient apiClient = new(http, logger != null 
        ? new LoggerAdapter<PrusaLinkApiClient>(logger) 
        : Microsoft.Extensions.Logging.Abstractions.NullLogger<PrusaLinkApiClient>.Instance);
    private readonly ILogger? _logger = logger;

    public async Task<PrusaCompositeStatus> GetCompositeStatusAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        try
        {
            var status = await apiClient.GetStatusAsync(baseUrl, apiKey, ct);
            var job = await apiClient.GetJobAsync(baseUrl, apiKey, ct);

            return new PrusaCompositeStatus(
                status?.Printer != null,
                status?.Printer?.State,
                job?.Progress,
                job?.File?.Name,
                null, // Thumbnail handling would need additional endpoint
                null, // Camera stream URL would need camera configuration
                null  // Camera snapshot URL would need camera configuration
            );
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get composite status from {BaseUrl}", baseUrl);
            return new PrusaCompositeStatus(false, null, null, null, null, null, null);
        }
    }

    public async Task<PrusaStatus> GetStatusAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        try
        {
            var status = await apiClient.GetStatusAsync(baseUrl, apiKey, ct);
            return new PrusaStatus(status?.Printer != null, status?.Printer?.State);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get status from {BaseUrl}", baseUrl);
            return new PrusaStatus(false, null);
        }
    }

    public async Task<PrusaJob?> GetJobAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        try
        {
            var job = await apiClient.GetJobAsync(baseUrl, apiKey, ct);
            if (job == null)
            {
                return null;
            }

            return new PrusaJob(
                job.State,
                job.Progress,
                job.File?.Name,
                null, // Thumbnail handling would need additional logic
                null, // Camera stream URL would need camera configuration  
                null  // Camera snapshot URL would need camera configuration
            );
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get job from {BaseUrl}", baseUrl);
            return null;
        }
    }

    // File upload and management methods - Using comprehensive API client
    public async Task<bool> UploadGcodeAsync(string baseUrl, string fileName, Stream fileContent, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            return await apiClient.UploadGcodeAsync(baseUrl, fileName, fileContent, apiKey, ct: ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to upload G-code file {FileName} to {BaseUrl}", fileName, baseUrl);
            return false;
        }
    }

    public async Task<bool> StartPrintAsync(string baseUrl, string fileName, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            return await apiClient.StartPrintAsync(baseUrl, fileName, apiKey, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start print of {FileName} on {BaseUrl}", fileName, baseUrl);
            return false;
        }
    }

    public async Task<string[]> GetFileListAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        try
        {
            return await apiClient.GetGcodeFilesAsync(baseUrl, apiKey, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to get file list from {BaseUrl}", baseUrl);
            return [];
        }
    }

    /// <summary>
    /// Access the underlying comprehensive API client for advanced operations
    /// </summary>
    public PrusaLinkApiClient ApiClient => apiClient;
}

public record PrusaStatus(bool IsOnline, string? State);
public record PrusaJob(string? PrintState, double? Progress, string? JobName, string? ThumbnailUrl, string? CameraStreamUrl, string? CameraSnapshotUrl);
public record PrusaCompositeStatus(
    bool IsOnline,
    string? State,
    double? Progress,
    string? JobName,
    string? ThumbnailUrl,
    string? CameraStreamUrl,
    string? CameraSnapshotUrl
);
