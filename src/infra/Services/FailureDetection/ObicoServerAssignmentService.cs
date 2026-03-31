using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// Assigns printers to Obico ML servers using a least-loaded strategy.
/// </summary>
public sealed class ObicoServerAssignmentService : IObicoServerAssignmentService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<ObicoServerAssignmentService> _logger;

    public ObicoServerAssignmentService(
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<ObicoServerAssignmentService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<ObicoServer?> AssignServerAsync(Guid printerId, CancellationToken ct = default)
    {
        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        Printer? printer = await db.Printers.FindAsync([printerId], ct);
        if (printer is null)
        {
            _logger.LogWarning("[ObicoAssignment] Printer {PrinterId} not found", printerId);
            return null;
        }

        ObicoServer? server = await PickBestServerAsync(db, ct);
        if (server is null)
        {
            _logger.LogWarning("[ObicoAssignment] No available Obico server for printer {PrinterName}", printer.Name);
            return null;
        }

        // Ensure ServiceState exists before assigning server
        if (printer.ServiceState == null)
        {
            printer.ServiceState = new PrinterServiceState { PrinterId = printer.Id };
        }

        printer.ServiceState.ObicoServerId = server.Id;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[ObicoAssignment] Assigned printer {PrinterName} to Obico server {ServerName} ({ServerUrl})",
            printer.Name, server.Name, server.Url);

        return server;
    }

    public async Task UnassignServerAsync(Guid printerId, CancellationToken ct = default)
    {
        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        Printer? printer = await db.Printers.FindAsync([printerId], ct);
        if (printer is null)
        {
            return;
        }

        string? previousServer = printer.ServiceState?.ObicoServerId?.ToString();
        if (printer.ServiceState != null)
        {
            printer.ServiceState.ObicoServerId = null;
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[ObicoAssignment] Unassigned printer {PrinterName} from Obico server {PreviousServerId}",
            printer.Name, previousServer ?? "none");
    }

    public async Task<int> RebalanceAsync(CancellationToken ct = default)
    {
        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        List<Printer> obicoEnabledPrinters = await db.Printers
            .Where(p => p.ObicoEnabled)
            .ToListAsync(ct);

        List<ObicoServer> enabledServers = await db.ObicoServers
            .Where(s => s.IsEnabled)
            .Include(s => s.Printers)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        if (enabledServers.Count == 0)
        {
            _logger.LogWarning("[ObicoAssignment] Rebalance: No enabled Obico servers");
            return 0;
        }

        int reassigned = 0;

        foreach (Printer printer in obicoEnabledPrinters)
        {
            // Find the server with the fewest assigned printers that still has capacity
            ObicoServer? bestServer = enabledServers
                .Where(s => s.Printers.Count < s.MaxConcurrentAnalyses)
                .OrderBy(s => s.Printers.Count)
                .FirstOrDefault();

            if (bestServer is null)
            {
                _logger.LogWarning(
                    "[ObicoAssignment] Rebalance: All servers at capacity, cannot assign printer {PrinterName}",
                    printer.Name);
                continue;
            }

            if (printer.ServiceState?.ObicoServerId != bestServer.Id)
            {
                if (printer.ServiceState == null)
                {
                    printer.ServiceState = new PrinterServiceState { PrinterId = printer.Id };
                }

                printer.ServiceState.ObicoServerId = bestServer.Id;
                reassigned++;
            }
        }

        if (reassigned > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("[ObicoAssignment] Rebalance complete: {Reassigned} printers reassigned", reassigned);
        return reassigned;
    }

    /// <summary>
    /// Picks the enabled server with the most available capacity (least-loaded).
    /// </summary>
    private static async Task<ObicoServer?> PickBestServerAsync(AppDbContext db, CancellationToken ct)
    {
        // Get enabled servers with their current printer assignment counts
        List<ObicoServer> servers = await db.ObicoServers
            .Where(s => s.IsEnabled)
            .Include(s => s.Printers)
            .ToListAsync(ct);

        return servers
            .Where(s => s.Printers.Count < s.MaxConcurrentAnalyses)
            .OrderBy(s => s.Printers.Count)
            .FirstOrDefault();
    }
}
