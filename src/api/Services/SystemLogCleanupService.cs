using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Farm.Infrastructure.Data;

namespace Farm.Web.Api.Services;

public class SystemLogCleanupService(IServiceProvider serviceProvider, ILogger<SystemLogCleanupService> logger) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ILogger<SystemLogCleanupService> _logger = logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(6); // Run every 6 hours
    private readonly int _retentionDays = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
                var oldLogs = await db.SystemLogs.Where(l => l.Timestamp < cutoff).ToListAsync(stoppingToken);
                if (oldLogs.Count > 0)
                {
                    db.SystemLogs.RemoveRange(oldLogs);
                    await db.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation("SystemLogCleanupService: Deleted {Count} logs older than {Cutoff}", oldLogs.Count, cutoff);
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
