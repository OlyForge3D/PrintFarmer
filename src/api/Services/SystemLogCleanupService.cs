using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Farm.Web.Api.Data;

namespace Farm.Web.Api.Services;

public class SystemLogCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SystemLogCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(6); // Run every 6 hours
    private readonly int _retentionDays;

    public SystemLogCleanupService(IServiceProvider serviceProvider, ILogger<SystemLogCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _retentionDays = 30; // Default retention, can be made configurable
    }

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
