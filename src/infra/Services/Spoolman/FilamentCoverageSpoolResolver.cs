using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Parsing;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Mutations;
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
    AppDbContext? db = null,
    IMutationWatermarkReader? watermarkReader = null) : IFilamentCoverageSpoolResolver
{
    private const int MaxConcurrentSourceRequests = 4;

    internal const string ReasonSpoolmanUnconfigured = "spoolman-unconfigured";
    internal const string ReasonSourceUnavailable = "spool-source-unavailable";
    internal const string ReasonSpoolNotFound = "spool-not-found";

    private readonly ISpoolmanService _spoolmanService = spoolmanService;
    private readonly IBackendClientFactory _backendClientFactory = backendClientFactory;
    private readonly ILogger<FilamentCoverageSpoolResolver> _logger = logger;
    private readonly AppDbContext? _db = db;
    private readonly IMutationWatermarkReader? _watermarkReader = watermarkReader;

    public async Task<FilamentCoverageSpoolSnapshot> ResolveSpoolAsync(
        CanonicalSpoolIdentity identity,
        CancellationToken ct)
    {
        long? originWatermark = await OriginWatermark
            .CaptureAsync(_watermarkReader, _logger, "source-qualified filament spool", ct)
            .ConfigureAwait(false);
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

            resolved = await ResolveCentralAsync(spoolIds, originWatermark, ct).ConfigureAwait(false);
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

                string nativeBaseUrl = identity.SourceIdentity.EndsWith('/')
                    ? identity.SourceIdentity
                    : identity.SourceIdentity + "/";
                resolved = await ResolveNativeAsync(
                    new SourceRequest(native, nativeBaseUrl)
                    {
                        SpoolIds = { identity.SpoolId },
                    },
                    originWatermark,
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
                    LogSanitizer.Sanitize(identity.SourceIdentity));
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
        long? originWatermark = await OriginWatermark
            .CaptureAsync(_watermarkReader, _logger, "filament spool source", ct)
            .ConfigureAwait(false);

        SourceSelection selection = SelectSource(printer);
        if (selection.ErrorReason is not null)
        {
            return new FilamentCoverageSpoolSnapshot(
                null,
                selection.Key.Native,
                selection.ErrorReason,
                OriginWatermark: null);
        }

        var request = new SourceRequest(selection.NativeClient, selection.ServerUrl);
        _ = request.SpoolIds.Add(spoolId);
        Dictionary<int, FilamentCoverageSpoolSnapshot> resolved = selection.Key.Native
            ? await ResolveNativeAsync(request, originWatermark, ct).ConfigureAwait(false)
            : await ResolveCentralAsync(request.SpoolIds, originWatermark, ct).ConfigureAwait(false);

        return resolved.TryGetValue(spoolId, out FilamentCoverageSpoolSnapshot? snapshot)
            ? snapshot
            : new FilamentCoverageSpoolSnapshot(
                null,
                selection.Key.Native,
                ReasonSpoolNotFound,
                OriginWatermark: null);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>>> ResolveAsync(
        IReadOnlyList<Printer> printers,
        CancellationToken ct)
    {
        long? originWatermark = await OriginWatermark
            .CaptureAsync(_watermarkReader, _logger, "filament spool sources", ct)
            .ConfigureAwait(false);
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

        // Distinct spool sources are independent HTTP adapters. Resolve them in
        // parallel without allowing a large farm to create unbounded fan-out.
        using SemaphoreSlim sourceRequestGate = new(MaxConcurrentSourceRequests);
        Task<KeyValuePair<SourceKey, Dictionary<int, FilamentCoverageSpoolSnapshot>>>[] pendingSources =
            requests.Select(async pair =>
            {
                await sourceRequestGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    Dictionary<int, FilamentCoverageSpoolSnapshot> resolved = pair.Key.Native
                        ? await ResolveNativeAsync(pair.Value, originWatermark, ct).ConfigureAwait(false)
                        : await ResolveCentralAsync(pair.Value.SpoolIds, originWatermark, ct).ConfigureAwait(false);
                    return KeyValuePair.Create(pair.Key, resolved);
                }
                finally
                {
                    _ = sourceRequestGate.Release();
                }
            }).ToArray();
        KeyValuePair<SourceKey, Dictionary<int, FilamentCoverageSpoolSnapshot>>[] completedSources =
            await Task.WhenAll(pendingSources).ConfigureAwait(false);
        Dictionary<SourceKey, Dictionary<int, FilamentCoverageSpoolSnapshot>> resolvedSources =
            completedSources.ToDictionary();

        Dictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result = [];
        foreach (Printer printer in printers)
        {
            SourceAssignment assignment = assignments[printer.Id];
            Dictionary<int, FilamentCoverageSpoolSnapshot> printerSpools = [];

            foreach (int spoolId in assignment.SpoolIds)
            {
                if (assignment.ErrorReason is not null)
                {
                    printerSpools[spoolId] = new(null, false, assignment.ErrorReason, OriginWatermark: null);
                }
                else if (resolvedSources.TryGetValue(assignment.Key, out Dictionary<int, FilamentCoverageSpoolSnapshot>? source)
                    && source.TryGetValue(spoolId, out FilamentCoverageSpoolSnapshot? snapshot))
                {
                    printerSpools[spoolId] = snapshot;
                }
                else
                {
                    printerSpools[spoolId] = new(null, assignment.Key.Native, ReasonSpoolNotFound, OriginWatermark: null);
                }
            }

            result[printer.Id] = printerSpools;
        }

        return result;
    }

    private async Task<Dictionary<int, FilamentCoverageSpoolSnapshot>> ResolveNativeAsync(
        SourceRequest request,
        long? originWatermark,
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
            return BuildSnapshots(request.SpoolIds, spools, true, originWatermark);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FilamentCoverage] Native Spoolman source unavailable at {ServerUrl}", LogSanitizer.Sanitize(request.ServerUrl));
            return Failure(request.SpoolIds, true, ReasonSourceUnavailable);
        }
    }

    private async Task<Dictionary<int, FilamentCoverageSpoolSnapshot>> ResolveCentralAsync(
        HashSet<int> spoolIds,
        long? originWatermark,
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

                foreach (SpoolmanSpoolDto spool in page.Items.Where(spool => spoolIds.Contains(spool.Id)))
                {
                    found[spool.Id] = spool;
                }

                totalCount = page.TotalCount;
                offset += pageSize;
            }
            while (found.Count < spoolIds.Count && offset < totalCount);

            return BuildSnapshots(spoolIds, found, false, originWatermark);
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
        bool tracksLiveConsumption,
        long? originWatermark)
        => spoolIds.ToDictionary(
            id => id,
            id => spools.TryGetValue(id, out SpoolmanSpoolDto? spool)
                ? new FilamentCoverageSpoolSnapshot(spool, tracksLiveConsumption, null, originWatermark)
                : new FilamentCoverageSpoolSnapshot(null, tracksLiveConsumption, ReasonSpoolNotFound, OriginWatermark: null));

    private static Dictionary<int, FilamentCoverageSpoolSnapshot> Failure(
        IEnumerable<int> spoolIds,
        bool tracksLiveConsumption,
        string reason)
        => spoolIds.ToDictionary(
            id => id,
            _ => new FilamentCoverageSpoolSnapshot(null, tracksLiveConsumption, reason, OriginWatermark: null));

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
