using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Parsing;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Source-aware spool resolver for filament coverage.
/// </summary>
public sealed class FilamentCoverageSpoolResolver(
    ISpoolmanService spoolmanService,
    IBackendClientFactory backendClientFactory,
    ILogger<FilamentCoverageSpoolResolver> logger) : IFilamentCoverageSpoolResolver
{
    internal const string ReasonSpoolmanUnconfigured = "spoolman-unconfigured";
    internal const string ReasonSourceUnavailable = "spool-source-unavailable";
    internal const string ReasonSpoolNotFound = "spool-not-found";

    private readonly ISpoolmanService _spoolmanService = spoolmanService;
    private readonly IBackendClientFactory _backendClientFactory = backendClientFactory;
    private readonly ILogger<FilamentCoverageSpoolResolver> _logger = logger;

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>>> ResolveAsync(
        IReadOnlyList<Printer> printers,
        CancellationToken ct)
    {
        Dictionary<Guid, SourceAssignment> assignments = [];
        Dictionary<SourceKey, SourceRequest> requests = [];

        foreach (Printer printer in printers)
        {
            HashSet<int> spoolIds = printer.Toolheads
                .Where(t => t.CurrentSpoolId.HasValue)
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

            if (printer.Backend == (int)PrinterBackend.Moonraker)
            {
                try
                {
                    IBackendClient client = _backendClientFactory.GetClient(printer.Backend);
                    if (client is not ISupportsSpoolman native)
                    {
                        assignments[printer.Id] = new SourceAssignment(default, spoolIds, ReasonSourceUnavailable);
                        continue;
                    }

                    SourceKey key = new(true, printer.Backend, NormalizeSource(printer.ServerUrl));
                    assignments[printer.Id] = new SourceAssignment(key, spoolIds, null);
                    if (!requests.TryGetValue(key, out SourceRequest? request))
                    {
                        request = new SourceRequest(native, printer.ServerUrl);
                        requests[key] = request;
                    }

                    request.SpoolIds.UnionWith(spoolIds);
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[FilamentCoverage] Backend source unavailable for printer {PrinterId}", printer.Id);
                    assignments[printer.Id] = new SourceAssignment(default, spoolIds, ReasonSourceUnavailable);
                    continue;
                }
            }

            SourceKey centralKey = new(false, 0, "central");
            assignments[printer.Id] = new SourceAssignment(centralKey, spoolIds, null);
            if (!requests.TryGetValue(centralKey, out SourceRequest? central))
            {
                central = new SourceRequest(null, null);
                requests[centralKey] = central;
            }

            central.SpoolIds.UnionWith(spoolIds);
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
        => serverUrl.Trim().TrimEnd('/');

    private readonly record struct SourceKey(bool Native, int Backend, string Identity);

    private sealed record SourceAssignment(SourceKey Key, HashSet<int> SpoolIds, string? ErrorReason);

    private sealed class SourceRequest(ISupportsSpoolman? nativeClient, string? serverUrl)
    {
        public ISupportsSpoolman? NativeClient { get; } = nativeClient;

        public string? ServerUrl { get; } = serverUrl;

        public HashSet<int> SpoolIds { get; } = [];
    }
}
