using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.Artifacts;

/// <summary>
/// Background hosted service that periodically runs artifact cleanup.
/// </summary>
public class ArtifactCleanupHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ArtifactCleanupHostedService> _logger;
    private readonly ArtifactStorageSettings _settings;

    public ArtifactCleanupHostedService(
        IServiceProvider serviceProvider,
        IOptions<ArtifactStorageSettings> opts,
        ILogger<ArtifactCleanupHostedService> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _settings = opts?.Value ?? throw new ArgumentNullException(nameof(opts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Artifact cleanup service started (Interval: {IntervalHours} hours, DryRun: {DryRun})",
            _settings.CleanupIntervalHours,
            _settings.EnableCleanupDryRun);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for the configured interval before first run
                await Task.Delay(TimeSpan.FromHours(_settings.CleanupIntervalHours), stoppingToken);

                _logger.LogInformation("Running scheduled artifact cleanup");

                // Create a scope to resolve scoped dependencies (DbContext)
                using IServiceScope scope = _serviceProvider.CreateScope();
                IArtifactCleanupService cleanupService = scope.ServiceProvider.GetRequiredService<IArtifactCleanupService>();

                int deletedCount = await cleanupService.ScanAndCleanupAsync(stoppingToken);

                _logger.LogInformation("Cleanup cycle completed: {DeletedCount} artifacts processed", deletedCount);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Artifact cleanup service is stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during artifact cleanup cycle");
                // Continue running despite errors
            }
        }

        _logger.LogInformation("Artifact cleanup service stopped");
    }
}
