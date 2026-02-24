using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Repositories.SystemLogs;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Infrastructure.Services.SystemLogs;

public class SystemLogCleanupService(IServiceScopeFactory scopeFactory, IUnifiedLoggingService logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                ISystemLogRepository logRepository = scope.ServiceProvider.GetRequiredService<ISystemLogRepository>();
                ISettingsService settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

                SystemLogSettings settings = settingsService.Get<SystemLogSettings>();
                int retentionDays = settings.RetentionDays;

                DateTime cutoff = DateTime.UtcNow.AddDays(-retentionDays);
                int deletedCount = await logRepository.DeleteLogsOlderThanAsync(cutoff, stoppingToken);
                if (deletedCount > 0)
                {
                    _logger.LogInformation($"SystemLogCleanupService: Deleted {deletedCount} logs older than {retentionDays} days");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SystemLogCleanupService: Error during log cleanup");
            }

            await Task.Delay(_cleanupInterval, stoppingToken);
        }
    }
}
