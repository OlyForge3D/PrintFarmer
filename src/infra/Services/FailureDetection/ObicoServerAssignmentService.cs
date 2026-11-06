using System.Data;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// Reserves Obico capacity for print lifetimes. Serializable reconciliation protects the
/// shared capacity budget across monitor instances; network probes never hold database locks.
/// </summary>
public sealed class ObicoServerAssignmentService(
    IDbContextFactory<AppDbContext> dbFactory,
    IObicoConnectivityProbe connectivityProbe,
    ILogger<ObicoServerAssignmentService> logger) : IObicoServerAssignmentService
{
    /// <inheritdoc />
    public async Task<ObicoPrintRoute> GetRouteAsync(Guid printerId, ObicoSettings settings, CancellationToken ct = default)
    {
        if (!settings.Enabled)
        {
            return Unavailable("Obico is disabled in Settings.");
        }

        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        PrinterServiceState? state = await db.PrinterServiceStates.AsNoTracking()
            .Include(candidate => candidate.ObicoServer)
            .SingleOrDefaultAsync(candidate => candidate.PrinterId == printerId, ct);
        if (state?.ObicoPrintKey is not null)
        {
            if (state.ObicoPrintKey.StartsWith("job:", StringComparison.Ordinal)
                && Guid.TryParseExact(state.ObicoPrintKey[4..], "N", out Guid jobId)
                && !await db.PrintJobs.WhereOccupiesPrinter().AnyAsync(job => job.Id == jobId, ct))
            {
                return Unavailable("The tracked print has ended; no active Obico reservation is available.");
            }

            if (state.ObicoServer is { } server)
            {
                ObicoConnectivityResult health = server.IsEnabled
                    ? await connectivityProbe.ProbeAsync(server.Url, server.ApiKey, ct)
                    : new(false, 0, "disabled");
                return ResolveRoute(state, settings, [server], [server],
                    new Dictionary<Guid, ObicoConnectivityResult> { [server.Id] = health }, null);
            }

            if (state.ObicoServerId is not null)
            {
                return ResolveRoute(state, settings, [], [], [], null);
            }

            if (state.ObicoFallbackUrl is not null)
            {
                ObicoConnectivityResult? health = state.ObicoFallbackUrl == settings.ObicoApiUrl
                    ? await connectivityProbe.ProbeAsync(state.ObicoFallbackUrl, null, ct)
                    : null;
                return ResolveRoute(state, settings, [], [], [], health);
            }
        }

        if (await db.ObicoServers.AnyAsync(ct))
        {
            return Unavailable("No active print has reserved an Obico server. Manual analysis cannot bypass the pool capacity budget.");
        }

        if (!string.IsNullOrWhiteSpace(settings.ObicoApiUrl))
        {
            ObicoConnectivityResult health = await connectivityProbe.ProbeAsync(settings.ObicoApiUrl, null, ct);
            if (health.Healthy)
            {
                return new("global", settings.ObicoApiUrl, settings.ObicoApiUrl, null);
            }
        }

        return Unavailable("No Obico server is available for manual analysis.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, ObicoPrintRoute>> ReconcileAsync(
        IReadOnlyCollection<ObicoPrintActivity> activities,
        ObicoSettings settings,
        CancellationToken ct = default)
    {
        await using AppDbContext probeDb = await dbFactory.CreateDbContextAsync(ct);
        List<ObicoServer> probeServers = settings.Enabled
            ? await probeDb.ObicoServers.AsNoTracking().ToListAsync(ct)
            : [];
        Dictionary<Guid, ObicoConnectivityResult> health = [];
        ObicoConnectivityResult? globalHealth = null;
        if (settings.Enabled)
        {
            var results = await Task.WhenAll(probeServers.Where(server => server.IsEnabled)
                .Select(async server => (server.Id, Result: await connectivityProbe.ProbeAsync(server.Url, server.ApiKey, ct))));
            health = results.ToDictionary(result => result.Id, result => result.Result);
            bool hasGlobalPrint = await probeDb.PrinterServiceStates.AsNoTracking()
                .AnyAsync(state => state.ObicoPrintKey != null && state.ObicoFallbackUrl == settings.ObicoApiUrl, ct);
            if ((probeServers.Count == 0 || hasGlobalPrint) && !string.IsNullOrWhiteSpace(settings.ObicoApiUrl))
            {
                globalHealth = await connectivityProbe.ProbeAsync(settings.ObicoApiUrl, null, ct);
            }
        }

        int attempt = 0;
        while (true)
        {
            try
            {
                await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
                return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    return await ReconcileCoreAsync(db, activities, settings, probeServers, health, globalHealth, ct);
                });
            }
            catch (Exception ex) when (attempt < 2 && IsContention(ex))
            {
                logger.LogWarning(ex, "[ObicoRouting] Concurrent route update; retrying reconciliation");
                attempt++;
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct);
            }
        }
    }

    private static bool IsContention(Exception exception) =>
        exception is DbUpdateConcurrencyException
        || DispatchTransientDatabaseFailure.IsTransientContention(exception)
        || (exception is DbUpdateException { InnerException: { } inner }
            && DispatchTransientDatabaseFailure.IsTransientContention(inner));

    private async Task<IReadOnlyDictionary<Guid, ObicoPrintRoute>> ReconcileCoreAsync(
        AppDbContext db,
        IReadOnlyCollection<ObicoPrintActivity> activities,
        ObicoSettings settings,
        List<ObicoServer> probeServers,
        Dictionary<Guid, ObicoConnectivityResult> health,
        ObicoConnectivityResult? globalHealth,
        CancellationToken ct)
    {
        // This is a writer transaction. SQLite must reserve its write lock before reading
        // capacity, rather than letting concurrent deferred readers deadlock on promotion.
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        List<ObicoServer> servers = await db.ObicoServers.AsNoTracking().ToListAsync(ct);
        List<PrinterServiceState> states = await db.PrinterServiceStates.ToListAsync(ct);
        HashSet<Guid> enabledPrinters = settings.Enabled
            ? (await db.Printers.Where(printer => printer.ObicoEnabled).Select(printer => printer.Id).ToListAsync(ct)).ToHashSet()
            : [];
        HashSet<string> activeJobKeys = (await db.PrintJobs.WhereOccupiesPrinter()
            .Select(job => job.Id).ToListAsync(ct))
            .Select(id => $"job:{id:N}").ToHashSet(StringComparer.Ordinal);
        Dictionary<Guid, ObicoPrintActivity> observations = activities.ToDictionary(activity => activity.PrinterId);
        Dictionary<Guid, PrinterServiceState> statesByPrinter = states.ToDictionary(state => state.PrinterId);

        // Clear legacy permanent pins and every confirmed end before counting capacity.
        foreach (PrinterServiceState state in states)
        {
            observations.TryGetValue(state.PrinterId, out ObicoPrintActivity? activity);
            if (!enabledPrinters.Contains(state.PrinterId)
                || state.ObicoPrintKey is null
                || (state.ObicoPrintKey.StartsWith("job:", StringComparison.Ordinal)
                    && !activeJobKeys.Contains(state.ObicoPrintKey))
                || (activity is not null && !activity.RetainExisting
                    && state.ObicoPrintKey != activity.PrintKey
                    && state.ObicoPrintKey != activity.AdoptExternalKey))
            {
                ClearRoute(state);
            }
        }

        Dictionary<Guid, int> loads = states.Where(state => state.ObicoServerId.HasValue)
            .GroupBy(state => state.ObicoServerId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        Dictionary<Guid, ObicoPrintRoute> routes = [];
        foreach (ObicoPrintActivity activity in activities.OrderBy(activity => activity.PrinterId))
        {
            if (!enabledPrinters.Contains(activity.PrinterId))
            {
                continue;
            }

            statesByPrinter.TryGetValue(activity.PrinterId, out PrinterServiceState? state);
            if (activity.RetainExisting || activity.PrintKey is null)
            {
                routes[activity.PrinterId] = Unavailable("Printer activity is unknown or no print is active.");
                continue;
            }

            if (activity.PrintKey.StartsWith("job:", StringComparison.Ordinal) && !activeJobKeys.Contains(activity.PrintKey))
            {
                routes[activity.PrinterId] = Unavailable("The tracked print ended before monitoring reconciliation.");
                continue;
            }

            if (state is null)
            {
                state = new PrinterServiceState { PrinterId = activity.PrinterId };
                db.PrinterServiceStates.Add(state);
                statesByPrinter.Add(activity.PrinterId, state);
            }

            state.ObicoPrintKey = activity.PrintKey;
            if (state.ObicoServerId is null && state.ObicoFallbackUrl is null)
            {
                ObicoServer? selected = servers
                    .Where(server => IsHealthy(server, probeServers, health)
                        && loads.GetValueOrDefault(server.Id) < server.MaxConcurrentAnalyses)
                    .OrderBy(server => (double)loads.GetValueOrDefault(server.Id) / server.MaxConcurrentAnalyses)
                    .ThenBy(server => loads.GetValueOrDefault(server.Id))
                    .ThenBy(server => server.Id)
                    .FirstOrDefault();
                if (selected is not null)
                {
                    state.ObicoServerId = selected.Id;
                    loads[selected.Id] = loads.GetValueOrDefault(selected.Id) + 1;
                    logger.LogInformation(
                        "[ObicoRouting] Reserved server {ServerId} for print {PrintKey} on printer {PrinterId}",
                        selected.Id, activity.PrintKey, activity.PrinterId);
                }
                else if (servers.Count == 0 && globalHealth?.Healthy == true)
                {
                    state.ObicoFallbackUrl = settings.ObicoApiUrl;
                }
            }

            ObicoPrintRoute route = ResolveRoute(state, settings, servers, probeServers, health, globalHealth);

            routes[activity.PrinterId] = route;
            if (route.UnavailableReason is not null)
            {
                logger.LogWarning(
                    "[ObicoRouting] Monitoring unavailable for printer {PrinterId}: {Reason}",
                    activity.PrinterId, route.UnavailableReason);
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return routes;
    }

    private static ObicoPrintRoute ResolveRoute(
        PrinterServiceState state,
        ObicoSettings settings,
        List<ObicoServer> servers,
        List<ObicoServer> probeServers,
        Dictionary<Guid, ObicoConnectivityResult> health,
        ObicoConnectivityResult? globalHealth)
    {
        if (state.ObicoServerId is Guid serverId)
        {
            ObicoServer? server = servers.Find(candidate => candidate.Id == serverId);
            return server is not null && IsHealthy(server, probeServers, health)
                ? new("pooled", server.Name, server.Url, server.ApiKey)
                : new("pooled", server?.Name, null, null,
                    "Selected Obico server is unavailable or disabled. Reservation retained; monitoring will retry this server. Printing continues.");
        }

        if (state.ObicoFallbackUrl is not null)
        {
            // A changed global URL is not implicitly substituted into an ongoing print.
            bool available = state.ObicoFallbackUrl == settings.ObicoApiUrl && globalHealth?.Healthy == true;
            return new("global", state.ObicoFallbackUrl,
                available ? state.ObicoFallbackUrl : null, null,
                available ? null : "Selected global Obico server is unavailable or changed. Printing continues without Obico monitoring.");
        }

        return Unavailable("No enabled, healthy Obico server has available print capacity. Monitoring will retry; printing continues.");
    }

    private static bool IsHealthy(
        ObicoServer server,
        List<ObicoServer> probed,
        Dictionary<Guid, ObicoConnectivityResult> health) =>
        server.IsEnabled && server.MaxConcurrentAnalyses > 0
        && health.GetValueOrDefault(server.Id)?.Healthy == true
        && probed.Any(candidate => candidate.Id == server.Id
            && candidate.Url == server.Url && candidate.ApiKey == server.ApiKey);

    private static ObicoPrintRoute Unavailable(string reason) => new("none", null, null, null, reason);

    private static void ClearRoute(PrinterServiceState state)
    {
        state.ObicoServerId = null;
        state.ObicoPrintKey = null;
        state.ObicoFallbackUrl = null;
    }
}
