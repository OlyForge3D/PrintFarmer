using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services;

public class MoonrakerDiagnosticsService : IMoonrakerDiagnosticsService
{
    private readonly IMoonrakerClient _moonrakerClient;
    private readonly IUnifiedLoggingService _logger;

    public MoonrakerDiagnosticsService(IMoonrakerClient moonrakerClient, IUnifiedLoggingService logger)
    {
        _moonrakerClient = moonrakerClient;
        _logger = logger;
    }

    private async Task<T?> ExecuteWithRetriesAsync<T>(Func<Task<T>> func)
    {
        const int MaxRetries = 3;
        const int InitialDelayMs = 500;

        int retryCount = 0;
        Exception? lastException = null;

        while (retryCount < MaxRetries)
        {
            try
            {
                if (retryCount > 0)
                {
                    int delay = InitialDelayMs * (int)Math.Pow(2, retryCount - 1);
                    _logger.LogInformation($"Retry {retryCount}/{MaxRetries} after {delay}ms", null, null);
                    await Task.Delay(delay);
                }

                return await func();
            }
            catch (Exception ex)
            {
                lastException = ex;
                retryCount++;
                _logger.LogWarning(ex, $"Attempt {retryCount}/{MaxRetries} failed", null, null);
            }
        }

        _logger.LogError(lastException!, "Operation failed after retries", null, null);
        return default;
    }

    public Task<FileRoot[]?> GetFileRootsAsync(string url)
    {
        return ExecuteWithRetriesAsync(() => _moonrakerClient.GetFileRootsAsync(url));
    }

    public Task<MoonrakerDirectoryInfo?> GetDirectoryAsync(string url, string path = "gcodes")
    {
        return ExecuteWithRetriesAsync(() => _moonrakerClient.GetDirectoryAsync(url, path, extended: true));
    }

    public Task<MoonrakerFileInfo[]?> GetDetailedFileListAsync(string url, string root = "gcodes", string? path = null)
    {
        return ExecuteWithRetriesAsync(() => _moonrakerClient.GetDetailedFileListAsync(url, root, path));
    }
}
