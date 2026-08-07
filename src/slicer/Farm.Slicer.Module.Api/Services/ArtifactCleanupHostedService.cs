using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Settings;
using Farm.Slicer.Module.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Background hosted service that runs artifact reconciliation at startup and cleanup periodically.
/// </summary>
public class ArtifactCleanupHostedService(
    IServiceProvider serviceProvider,
    IOptions<ArtifactStorageSettings> opts,
    ILogger<ArtifactCleanupHostedService> logger) : BackgroundService
{
    private const int MaximumCleanupIntervalHours = 24 * 7;

    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ILogger<ArtifactCleanupHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ArtifactStorageSettings _settings = opts?.Value ?? throw new ArgumentNullException(nameof(opts));
    private readonly int _cleanupIntervalHours =
        NormalizeCleanupIntervalHours(opts?.Value.CleanupIntervalHours ?? 24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_cleanupIntervalHours != _settings.CleanupIntervalHours)
        {
            _logger.LogWarning(
                "Artifact cleanup interval {ConfiguredHours} hours is outside the supported range; using {EffectiveHours} hours",
                _settings.CleanupIntervalHours,
                _cleanupIntervalHours);
        }

        _logger.LogInformation(
            "Artifact cleanup service started (Interval: {IntervalHours} hours, DryRun: {DryRun})",
            _cleanupIntervalHours,
            _settings.EnableCleanupDryRun);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Running artifact cleanup");

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

            try
            {
                await Task.Delay(
                    TimeSpan.FromHours(_cleanupIntervalHours),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Artifact cleanup service is stopping");
                break;
            }
        }

        _logger.LogInformation("Artifact cleanup service stopped");
    }

    internal static int NormalizeCleanupIntervalHours(int configuredHours) =>
        Math.Clamp(configuredHours, 1, MaximumCleanupIntervalHours);
}
