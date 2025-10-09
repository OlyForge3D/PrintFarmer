using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
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
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                DateTime cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
                List<Farm.Infrastructure.Domain.SystemLog> oldLogs = await db.SystemLogs.Where(l => l.Timestamp < cutoff).ToListAsync(stoppingToken);
                if (oldLogs.Count > 0)
                {
                    db.SystemLogs.RemoveRange(oldLogs);
                    _ = await db.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation($"SystemLogCleanupService: Deleted {oldLogs.Count} logs older than {cutoff}");
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
