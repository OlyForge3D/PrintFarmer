using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services;

/// <summary>
/// Hosted service that integrates with ASP.NET Core's application lifecycle to properly await completion of background harvest tasks during shutdown
/// </summary>
public class GracefulShutdownService(
    IServiceProvider serviceProvider,
    IHostApplicationLifetime appLifetime,
    IUnifiedLoggingService logger) : IHostedService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IHostApplicationLifetime _appLifetime = appLifetime;
    private readonly IUnifiedLoggingService _logger = logger;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = _appLifetime.ApplicationStopping.Register(() => _ = OnStoppingAsync());
        return Task.CompletedTask;
    }

    private async Task OnStoppingAsync()
    {
        _logger.LogInformation("Application is stopping. Waiting for background harvest tasks to complete...");

        try
        {
            await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
            IGcodeHarvestService harvestService = scope.ServiceProvider.GetRequiredService<IGcodeHarvestService>();

            // Wait up to 30 seconds for tasks to complete
            await harvestService.WaitForAllTasksAsync(TimeSpan.FromSeconds(30));
            _logger.LogInformation("All background harvest tasks completed successfully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timeout waiting for background harvest tasks to complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while waiting for background harvest tasks to complete");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
