using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Repositories.SystemLogs;
using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Web.Api.Services;

public class SystemLogCleanupService(IServiceScopeFactory scopeFactory, IUnifiedLoggingService logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(6); // Run every 6 hours
    private readonly int _retentionDays = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                ISystemLogRepository logRepository = scope.ServiceProvider.GetRequiredService<ISystemLogRepository>();
                DateTime cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
                int deletedCount = await logRepository.DeleteLogsOlderThanAsync(cutoff, stoppingToken);
                if (deletedCount > 0)
                {
                    _logger.LogInformation($"SystemLogCleanupService: Deleted {deletedCount} logs older than {cutoff}");
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
