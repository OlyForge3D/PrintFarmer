using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Parsing;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
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
    IMutationWatermarkReader? watermarkReader = null,
    IPrinterStatusCacheReader? statusCache = null,
    ISettingsService? settingsService = null) : IFilamentCoverageSpoolResolver
{
    /// <summary>
    /// Upper bound on spool sources resolved at once. Distinct sources are
    /// independent hosts, so this exists only to stop a large farm creating
    /// unbounded fan-out against the network.
    ///
    /// <para>
    /// This is NOT what keeps the endpoint inside the mobile client's readiness budget.
    /// Dark sources each hold a slot for the full per-source timeout, so N of them
    /// serialise into <c>ceil(N / MaxConcurrentSourceRequests)</c> waves and total latency
    /// still grows with fleet size. <see cref="SpoolCoverageSettings.FleetResolveTimeoutMs"/>
    /// is what bounds the endpoint, at any fleet size.
    /// </para>
    /// </summary>
    private const int MaxConcurrentSourceRequests = 8;

    internal const string ReasonSpoolmanUnconfigured = "spoolman-unconfigured";
    internal const string ReasonSourceUnavailable = "spool-source-unavailable";
    internal const string ReasonSpoolNotFound = "spool-not-found";

    private readonly ISpoolmanService _spoolmanService = spoolmanService;
    private readonly IBackendClientFactory _backendClientFactory = backendClientFactory;
    private readonly ILogger<FilamentCoverageSpoolResolver> _logger = logger;
    private readonly AppDbContext? _db = db;
    private readonly IMutationWatermarkReader? _watermarkReader = watermarkReader;
    private readonly IPrinterStatusCacheReader? _statusCache = statusCache;
    private readonly ISettingsService? _settingsService = settingsService;

    /// <summary>
    /// The read deadlines for one resolve operation. Captured once per call so every
    /// source in a fan-out uses the same values: <c>SettingsService.Get</c> enumerates a
    /// shared dictionary, so reading it from each of the concurrent source tasks can race
    /// a concurrent settings save and fall back to defaults for some sources but not
    /// others.
    /// </summary>
    private readonly record struct SpoolReadBudget(TimeSpan PerSource, TimeSpan Fleet);

    /// <summary>
    /// Resolves the configured read deadlines, falling back to the
    /// <see cref="SpoolCoverageSettings"/> defaults when settings are unavailable.
    /// Coverage must never inherit the backend's print-control timeout for this
    /// read-only projection.
    /// </summary>
    private SpoolReadBudget ReadBudget()
    {
        SpoolCoverageSettings settings = new();
        try
        {
            if (_settingsService?.Get<SpoolCoverageSettings>() is SpoolCoverageSettings configured)
            {
                settings = configured;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FilamentCoverage] Falling back to default spool read budget");
        }

        // Clamp both ends. Validate() enforces these ranges on the write path, but a row
        // persisted by another path (a migration, a direct edit) must not be able to
        // reintroduce the very stall this budget exists to prevent.
        return new SpoolReadBudget(
            TimeSpan.FromMilliseconds(Math.Clamp(settings.SpoolSourceTimeoutMs, 250, 30_000)),
            TimeSpan.FromMilliseconds(Math.Clamp(settings.FleetResolveTimeoutMs, 1_000, 60_000)));
    }

    /// <summary>
    /// Renders a spool source's URL for logging with any embedded userinfo removed.
    /// <see cref="LogSanitizer"/> only defeats log forging; it does not strip credentials,
    /// and a server URL is free text an operator may have typed as
    /// <c>http://user:secret@host</c>.
    /// </summary>
    private static string? DescribeSource(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return LogSanitizer.Sanitize(serverUrl);
        }

        if (Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            UriBuilder redacted = new(uri) { UserName = string.Empty, Password = string.Empty };
            return LogSanitizer.Sanitize(redacted.Uri.ToString());
        }

        return LogSanitizer.Sanitize(serverUrl);
    }

    public async Task<FilamentCoverageSpoolSnapshot> ResolveSpoolAsync(
        CanonicalSpoolIdentity identity,
        CancellationToken ct)
    {
        long? originWatermark = await OriginWatermark
            .CaptureAsync(_watermarkReader, _logger, "source-qualified filament spool", ct)
            .ConfigureAwait(false);
        HashSet<int> spoolIds = [identity.SpoolId];

        // Single source, so the fleet budget adds nothing beyond the per-source deadline;
        // pass the caller token through as the budget token.
        SpoolReadBudget budget = ReadBudget();
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

            resolved = await ResolveCentralAsync(spoolIds, originWatermark, budget, ct, ct).ConfigureAwait(false);
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
                    budget,
                    ct,
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

        // Single source, so the fleet budget adds nothing beyond the per-source deadline;
        // pass the caller token through as the budget token.
        SpoolReadBudget budget = ReadBudget();
        Dictionary<int, FilamentCoverageSpoolSnapshot> resolved = selection.Key.Native
            ? await ResolveNativeAsync(request, originWatermark, budget, ct, ct).ConfigureAwait(false)
            : await ResolveCentralAsync(request.SpoolIds, originWatermark, budget, ct, ct).ConfigureAwait(false);

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

            // Fast-fail printers the status cache already knows are offline (#2118): a
            // powered-down printer that still holds its network address black-holes the
            // packet instead of refusing it, so attempting the read would stall this
            // source for a full timeout window. Skip the network round-trip entirely
            // rather than let it join the fan-out.
            if (selection.Key.Native
                && _statusCache?.GetStatus(printer.Id) is { IsOnline: false })
            {
                assignments[printer.Id] = new SourceAssignment(
                    selection.Key,
                    spoolIds,
                    ReasonSourceUnavailable);
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
        //
        // The gate alone cannot bound this endpoint: dark sources each hold a slot for
        // the full per-source timeout, so they serialise into successive timeout waves
        // and total latency grows with fleet size. The fleet deadline below is what makes
        // the bound hold at any size — when it expires, sources still in flight degrade
        // to "unavailable" and the projection returns the coverage it already has.
        SpoolReadBudget budget = ReadBudget();
        using CancellationTokenSource fleetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fleetCts.CancelAfter(budget.Fleet);
        CancellationToken budgetToken = fleetCts.Token;

        using SemaphoreSlim sourceRequestGate = new(MaxConcurrentSourceRequests);
        Task<KeyValuePair<SourceKey, Dictionary<int, FilamentCoverageSpoolSnapshot>>>[] pendingSources =
            requests.Select(async pair =>
            {
                try
                {
                    await sourceRequestGate.WaitAsync(budgetToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // The fleet deadline expired while this source was still queued behind
                    // the gate. Report it as unavailable rather than failing the projection.
                    return KeyValuePair.Create(pair.Key, Failure(pair.Value.SpoolIds, pair.Key.Native, ReasonSourceUnavailable));
                }
                catch (OperationCanceledException)
                {
                    // The CALLER cancelled while this source was still queued. WaitAsync throws
                    // carrying the linked budget token, so normalise to the caller's token and
                    // keep all three cancellation exits uniform.
                    //
                    // This is hardening, not a repair of observable behaviour: ResolveAsync
                    // surfaces cancellation through Task.WhenAll, which reports the LOWEST-INDEX
                    // cancelled task's token, and indices 0..MaxConcurrentSourceRequests-1 always
                    // win the gate synchronously and so are always in flight. A queued source can
                    // therefore never be the task whose token escapes. Normalising anyway means
                    // the guarantee does not silently depend on that Task.WhenAll ordering detail
                    // if the fan-out ever consumes sources in completion order instead.
                    ct.ThrowIfCancellationRequested();
                    throw;
                }

                try
                {
                    Dictionary<int, FilamentCoverageSpoolSnapshot> resolved = pair.Key.Native
                        ? await ResolveNativeAsync(pair.Value, originWatermark, budget, budgetToken, ct).ConfigureAwait(false)
                        : await ResolveCentralAsync(pair.Value.SpoolIds, originWatermark, budget, budgetToken, ct).ConfigureAwait(false);
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
        SpoolReadBudget budget,
        CancellationToken budgetToken,
        CancellationToken ct)
    {
        TimeSpan timeout = budget.PerSource;
        try
        {
            // A powered-down printer that still holds its address black-holes packets
            // instead of refusing them, so this read must carry its own deadline. Without
            // it the call inherits the backend's print-control timeout (60s) and one dark
            // printer stalls the entire fleet projection. Linking off the fleet budget (not
            // the caller token) also cuts this read short when the overall deadline expires.
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(budgetToken);
            linked.CancelAfter(timeout);

            string? json = await request.NativeClient!
                .GetSpoolmanSpoolsAsync(request.ServerUrl!, linked.Token)
                .ConfigureAwait(false);

            // The Moonraker client swallows every exception from its Spoolman proxy and
            // reports a cancelled or timed-out call as a null body, so cancellation has to
            // be re-surfaced explicitly. Checking the LINKED token covers both cases; the
            // catch filters below then separate our timeout from a caller cancellation.
            linked.Token.ThrowIfCancellationRequested();

            if (json is null)
            {
                return Failure(request.SpoolIds, true, ReasonSourceUnavailable);
            }

            Dictionary<int, SpoolmanSpoolDto> spools = SpoolmanJsonParser.ParseSpools(json)
                .GroupBy(s => s.Id)
                .ToDictionary(g => g.Key, g => g.First());
            return BuildSnapshots(request.SpoolIds, spools, true, originWatermark);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Either this source's own deadline or the fleet deadline. Both degrade; only a
            // genuine caller cancellation propagates, via the rethrow below.
            _logger.LogDebug(
                "[FilamentCoverage] Native Spoolman source timed out after {TimeoutMs}ms at {ServerUrl}",
                timeout.TotalMilliseconds,
                DescribeSource(request.ServerUrl));
            return Failure(request.SpoolIds, true, ReasonSourceUnavailable);
        }
        catch (OperationCanceledException)
        {
            // Reached only when the CALLER cancelled. Rethrow carrying the caller's token
            // rather than the internal linked one, so callers can identify their own
            // cancellation from ex.CancellationToken.
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FilamentCoverage] Native Spoolman source unavailable at {ServerUrl}", DescribeSource(request.ServerUrl));
            return Failure(request.SpoolIds, true, ReasonSourceUnavailable);
        }
    }

    private async Task<Dictionary<int, FilamentCoverageSpoolSnapshot>> ResolveCentralAsync(
        HashSet<int> spoolIds,
        long? originWatermark,
        SpoolReadBudget budget,
        CancellationToken budgetToken,
        CancellationToken ct)
    {
        SpoolmanConfigDto? config = _spoolmanService.GetConfig();
        if (config is null || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            return Failure(spoolIds, false, ReasonSpoolmanUnconfigured);
        }

        TimeSpan timeout = budget.PerSource;
        try
        {
            // Bound the whole paged read, not each page: an unreachable central Spoolman
            // must not stall coverage any longer than a dark printer does.
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(budgetToken);
            linked.CancelAfter(timeout);

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
                    linked.Token).ConfigureAwait(false);

                // SpoolmanService.ListSpoolsAsync catches every exception - including
                // cancellation - and returns an EMPTY page. Without this check a timed-out
                // read would fall through to BuildSnapshots and be reported as
                // `spool-not-found`, i.e. an affirmative "that spool does not exist" claim
                // about a source we never actually reached. Re-surface cancellation here so
                // the catch filters below degrade to `spool-source-unavailable` instead.
                linked.Token.ThrowIfCancellationRequested();

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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(
                "[FilamentCoverage] Central Spoolman source timed out after {TimeoutMs}ms",
                timeout.TotalMilliseconds);
            return Failure(spoolIds, false, ReasonSourceUnavailable);
        }
        catch (OperationCanceledException)
        {
            // Reached only when the CALLER cancelled. Rethrow carrying the caller's token
            // rather than the internal linked one, so callers can identify their own
            // cancellation from ex.CancellationToken.
            ct.ThrowIfCancellationRequested();
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
