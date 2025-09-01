using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services;

/// <summary>
/// Hosted service that integrates with ASP.NET Core's application lifecycle to properly await completion of background harvest tasks during shutdown
/// </summary>
public class GracefulShutdownService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<GracefulShutdownService> _logger;
    
    public GracefulShutdownService(
        IServiceProvider serviceProvider,
        IHostApplicationLifetime appLifetime,
        ILogger<GracefulShutdownService> logger)
    {
        _serviceProvider = serviceProvider;
        _appLifetime = appLifetime;
        _logger = logger;
    }
    
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _appLifetime.ApplicationStopping.Register(OnStopping);
        return Task.CompletedTask;
    }
    
    private async void OnStopping()
    {
        _logger.LogInformation("Application is stopping. Waiting for background harvest tasks to complete...");
        
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var harvestService = scope.ServiceProvider.GetRequiredService<IGcodeHarvestService>();
            
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