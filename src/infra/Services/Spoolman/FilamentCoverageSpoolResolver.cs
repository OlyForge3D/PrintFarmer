using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Parsing;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Source-aware spool resolver shared by filament coverage and guided spool binding.
/// </summary>
public sealed class FilamentCoverageSpoolResolver(
    ISpoolmanService spoolmanService,
    IBackendClientFactory backendClientFactory,
    ILogger<FilamentCoverageSpoolResolver> logger,
    AppDbContext? db = null) : IFilamentCoverageSpoolResolver
{
    internal const string ReasonSpoolmanUnconfigured = "spoolman-unconfigured";
    internal const string ReasonSourceUnavailable = "spool-source-unavailable";
    internal const string ReasonSpoolNotFound = "spool-not-found";

    private readonly ISpoolmanService _spoolmanService = spoolmanService;
    private readonly IBackendClientFactory _backendClientFactory = backendClientFactory;
    private readonly ILogger<FilamentCoverageSpoolResolver> _logger = logger;
    private readonly AppDbContext? _db = db;

    public async Task<FilamentCoverageSpoolSnapshot> ResolveSpoolAsync(
        CanonicalSpoolIdentity identity,
        CancellationToken ct)
    {
        HashSet<int> spoolIds = [identity.SpoolId];
        Dictionary<int, FilamentCoverageSpoolSnapshot> resolved;

        if (identity.SourceKind == SpoolSourceKind.Central)
        {
            SpoolmanConfigDto? config = _spoolmanService.GetConfig();
            if (config is null || string.IsNullOrWhiteSpace(config.BaseUrl))
            {
                return new FilamentCoverageSpoolSnapshot(
                    null,
                    false,
                    ReasonSpoolmanUnconfigured);
            }

            string configuredIdentity;
            try
            {
                configuredIdentity = CanonicalSpoolIdentity.NormalizeSourceIdentity(config.BaseUrl);
            }
            catch (ArgumentException)
            {
                return new FilamentCoverageSpoolSnapshot(
                    null,
                    false,
                    ReasonSourceUnavailable);
            }

            if (!string.Equals(
                    configuredIdentity,
                    identity.SourceIdentity,
                    StringComparison.Ordinal))
            {
                return new FilamentCoverageSpoolSnapshot(
                    null,
                    false,
                    ReasonSourceUnavailable);
            }

            resolved = await ResolveCentralAsync(spoolIds, ct).ConfigureAwait(false);
        }
        else
        {
            if (!await IsConfiguredNativeSourceAsync(identity.SourceIdentity, ct)
                    .ConfigureAwait(false))
            {
                return new FilamentCoverageSpoolSnapshot(
                    null,
                    true,
                    ReasonSourceUnavailable);
            }

            try
            {
                IBackendClient client = _backendClientFactory.GetClient((int)PrinterBackend.Moonraker);
                if (client is not ISupportsSpoolman native)
                {
                    return new FilamentCoverageSpoolSnapshot(
                        null,
                        true,
                        ReasonSourceUnavailable);
                }

                resolved = await ResolveNativeAsync(
                    new SourceRequest(native, identity.SourceIdentity)
                    {
                        SpoolIds = { identity.SpoolId },
                    },
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "[FilamentCoverage] Native Spoolman source unavailable at {ServerUrl}",
                    identity.SourceIdentity);
                return new FilamentCoverageSpoolSnapshot(
                    null,
                    true,
                    ReasonSourceUnavailable);
            }
        }

        return resolved.TryGetValue(identity.SpoolId, out FilamentCoverageSpoolSnapshot? snapshot)
            ? snapshot
            : new FilamentCoverageSpoolSnapshot(
                null,
                identity.SourceKind == SpoolSourceKind.MoonrakerNative,
                ReasonSpoolNotFound);
    }

    private async Task<bool> IsConfiguredNativeSourceAsync(
        string sourceIdentity,
        CancellationToken ct)
    {
        if (_db is null)
        {
            return false;
        }

        List<string> configuredUrls = await _db.Printers
            .AsNoTracking()
            .Where(printer => printer.Backend == (int)PrinterBackend.Moonraker)
            .Select(printer => printer.ServerUrl)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (string configuredUrl in configuredUrls)
        {
            try
            {
                string configuredIdentity =
                    CanonicalSpoolIdentity.NormalizeSourceIdentity(configuredUrl);
                if (string.Equals(
                        configuredIdentity,
                        sourceIdentity,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // Invalid configured URLs cannot authorize an outbound request.
            }
        }

        return false;
    }

    public async Task<FilamentCoverageSpoolSnapshot> ResolveSpoolAsync(
        Printer printer,
        int spoolId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(printer);

        SourceSelection selection = SelectSource(printer);
        if (selection.ErrorReason is not null)
        {
            return new FilamentCoverageSpoolSnapshot(
                null,
                selection.Key.Native,
                selection.ErrorReason);
        }

        var request = new SourceRequest(selection.NativeClient, selection.ServerUrl);
        _ = request.SpoolIds.Add(spoolId);
        Dictionary<int, FilamentCoverageSpoolSnapshot> resolved = selection.Key.Native
            ? await ResolveNativeAsync(request, ct).ConfigureAwait(false)
            : await ResolveCentralAsync(request.SpoolIds, ct).ConfigureAwait(false);

        return resolved.TryGetValue(spoolId, out FilamentCoverageSpoolSnapshot? snapshot)
            ? snapshot
            : new FilamentCoverageSpoolSnapshot(
                null,
                selection.Key.Native,
                ReasonSpoolNotFound);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>>> ResolveAsync(
        IReadOnlyList<Printer> printers,
        CancellationToken ct)
    {
        Dictionary<Guid, SourceAssignment> assignments = [];
        Dictionary<SourceKey, SourceRequest> requests = [];

        foreach (Printer printer in printers)
        {
            HashSet<int> spoolIds = printer.Toolheads
                .Where(t =>
                    ToolheadIndexMapper.IsFilamentSource(t, printer.Toolheads)
                    && t.CurrentSpoolId.HasValue)
                .Select(t => t.CurrentSpoolId!.Value)
                .ToHashSet();
            if (printer.CurrentSpoolId.HasValue)
            {
                _ = spoolIds.Add(printer.CurrentSpoolId.Value);
            }

            if (spoolIds.Count == 0)
            {
                assignments[printer.Id] = new SourceAssignment(default, spoolIds, null);
                continue;
            }

            SourceSelection selection = SelectSource(printer);
            if (selection.ErrorReason is not null)
            {
                assignments[printer.Id] = new SourceAssignment(
                    selection.Key,
                    spoolIds,
                    selection.ErrorReason);
                continue;
            }

            assignments[printer.Id] = new SourceAssignment(selection.Key, spoolIds, null);
            if (!requests.TryGetValue(selection.Key, out SourceRequest? request))
            {
                request = new SourceRequest(selection.NativeClient, selection.ServerUrl);
                requests[selection.Key] = request;
            }

            request.SpoolIds.UnionWith(spoolIds);
        }

        Dictionary<SourceKey, Dictionary<int, FilamentCoverageSpoolSnapshot>> resolvedSources = [];
        foreach ((SourceKey key, SourceRequest request) in requests)
        {
            resolvedSources[key] = key.Native
                ? await ResolveNativeAsync(request, ct).ConfigureAwait(false)
                : await ResolveCentralAsync(request.SpoolIds, ct).ConfigureAwait(false);
        }

        Dictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result = [];
        foreach (Printer printer in printers)
        {
            SourceAssignment assignment = assignments[printer.Id];
            Dictionary<int, FilamentCoverageSpoolSnapshot> printerSpools = [];

            foreach (int spoolId in assignment.SpoolIds)
            {
                if (assignment.ErrorReason is not null)
                {
                    printerSpools[spoolId] = new(null, false, assignment.ErrorReason);
                }
                else if (resolvedSources.TryGetValue(assignment.Key, out Dictionary<int, FilamentCoverageSpoolSnapshot>? source)
                    && source.TryGetValue(spoolId, out FilamentCoverageSpoolSnapshot? snapshot))
                {
                    printerSpools[spoolId] = snapshot;
                }
                else
                {
                    printerSpools[spoolId] = new(null, assignment.Key.Native, ReasonSpoolNotFound);
                }
            }

            result[printer.Id] = printerSpools;
        }

        return result;
    }

    private async Task<Dictionary<int, FilamentCoverageSpoolSnapshot>> ResolveNativeAsync(
        SourceRequest request,
        CancellationToken ct)
    {
        try
        {
            string? json = await request.NativeClient!
                .GetSpoolmanSpoolsAsync(request.ServerUrl!, ct)
                .ConfigureAwait(false);
            if (json is null)
            {
                return Failure(request.SpoolIds, true, ReasonSourceUnavailable);
            }

            Dictionary<int, SpoolmanSpoolDto> spools = SpoolmanJsonParser.ParseSpools(json)
                .GroupBy(s => s.Id)
                .ToDictionary(g => g.Key, g => g.First());
            return BuildSnapshots(request.SpoolIds, spools, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FilamentCoverage] Native Spoolman source unavailable at {ServerUrl}", request.ServerUrl);
            return Failure(request.SpoolIds, true, ReasonSourceUnavailable);
        }
    }

    private async Task<Dictionary<int, FilamentCoverageSpoolSnapshot>> ResolveCentralAsync(
        HashSet<int> spoolIds,
        CancellationToken ct)
    {
        SpoolmanConfigDto? config = _spoolmanService.GetConfig();
        if (config is null || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            return Failure(spoolIds, false, ReasonSpoolmanUnconfigured);
        }

        try
        {
            const int pageSize = 500;
            Dictionary<int, SpoolmanSpoolDto> found = [];
            int offset = 0;
            int totalCount;
            do
            {
                SpoolmanPagedResult<SpoolmanSpoolDto> page = await _spoolmanService.ListSpoolsAsync(
                    new SpoolmanSpoolQueryParams
                    {
                        Limit = pageSize,
                        Offset = offset,
                        AllowArchived = true,
                    },
                    ct).ConfigureAwait(false);

                foreach (SpoolmanSpoolDto spool in page.Items)
                {
                    if (spoolIds.Contains(spool.Id))
                    {
                        found[spool.Id] = spool;
                    }
                }

                totalCount = page.TotalCount;
                offset += pageSize;
            }
            while (found.Count < spoolIds.Count && offset < totalCount);

            return BuildSnapshots(spoolIds, found, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FilamentCoverage] Central Spoolman source unavailable");
            return Failure(spoolIds, false, ReasonSourceUnavailable);
        }
    }

    private static Dictionary<int, FilamentCoverageSpoolSnapshot> BuildSnapshots(
        IEnumerable<int> spoolIds,
        Dictionary<int, SpoolmanSpoolDto> spools,
        bool tracksLiveConsumption)
        => spoolIds.ToDictionary(
            id => id,
            id => spools.TryGetValue(id, out SpoolmanSpoolDto? spool)
                ? new FilamentCoverageSpoolSnapshot(spool, tracksLiveConsumption, null)
                : new FilamentCoverageSpoolSnapshot(null, tracksLiveConsumption, ReasonSpoolNotFound));

    private static Dictionary<int, FilamentCoverageSpoolSnapshot> Failure(
        IEnumerable<int> spoolIds,
        bool tracksLiveConsumption,
        string reason)
        => spoolIds.ToDictionary(
            id => id,
            _ => new FilamentCoverageSpoolSnapshot(null, tracksLiveConsumption, reason));

    private static string NormalizeSource(string serverUrl)
        => CanonicalSpoolIdentity.NormalizeSourceIdentity(serverUrl);

    private SourceSelection SelectSource(Printer printer)
    {
        if (printer.Backend != (int)PrinterBackend.Moonraker)
        {
            return new SourceSelection(new SourceKey(false, 0, "central"), null, null, null);
        }

        try
        {
            IBackendClient client = _backendClientFactory.GetClient(printer.Backend);
            if (client is not ISupportsSpoolman native)
            {
                return new SourceSelection(default, null, null, ReasonSourceUnavailable);
            }

            return new SourceSelection(
                new SourceKey(true, printer.Backend, NormalizeSource(printer.ServerUrl)),
                native,
                printer.ServerUrl,
                null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FilamentCoverage] Backend source unavailable for printer {PrinterId}", printer.Id);
            return new SourceSelection(default, null, null, ReasonSourceUnavailable);
        }
    }

    private readonly record struct SourceKey(bool Native, int Backend, string Identity);

    private sealed record SourceSelection(
        SourceKey Key,
        ISupportsSpoolman? NativeClient,
        string? ServerUrl,
        string? ErrorReason);

    private sealed record SourceAssignment(SourceKey Key, HashSet<int> SpoolIds, string? ErrorReason);

    private sealed class SourceRequest(ISupportsSpoolman? nativeClient, string? serverUrl)
    {
        public ISupportsSpoolman? NativeClient { get; } = nativeClient;

        public string? ServerUrl { get; } = serverUrl;

        public HashSet<int> SpoolIds { get; } = [];
    }
}
