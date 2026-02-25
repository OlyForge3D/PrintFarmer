using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Repositories.SystemLogs;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.SystemLogs;

public class SystemLogCleanupService(IServiceScopeFactory scopeFactory, ILogger<SystemLogCleanupService> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<SystemLogCleanupService> _logger = logger;
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
                    _logger.LogInformation("SystemLogCleanupService: Deleted {DeletedCount} logs older than {RetentionDays} days", deletedCount, retentionDays);
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
