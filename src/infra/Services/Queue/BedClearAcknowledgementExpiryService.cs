// <copyright file="BedClearAcknowledgementExpiryService.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Proactively expires or invalidates exact-job bed-clear acknowledgements so clients receive
/// the durable lifecycle event without waiting for another acknowledgement or dispatch call.
/// </summary>
public sealed class BedClearAcknowledgementExpiryService(
    IServiceScopeFactory scopeFactory,
    ILogger<BedClearAcknowledgementExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Bed-clear acknowledgement lifecycle scan failed.");
            }

            await Task.Delay(ScanInterval, stoppingToken);
        }
    }

    internal async Task ScanAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IBedClearAcknowledgementService service =
            scope.ServiceProvider.GetRequiredService<IBedClearAcknowledgementService>();
        List<Guid> printerIds = await db.PrinterDispatchStates
            .AsNoTracking()
            .Where(state => state.AcknowledgedJobId != null)
            .OrderBy(state => state.PrinterId)
            .Select(state => state.PrinterId)
            .Take(100)
            .ToListAsync(ct);
        foreach (Guid printerId in printerIds)
        {
            await service.InvalidateStaleAcknowledgementsAsync(printerId, ct);
        }
    }
}
