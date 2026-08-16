using System.Collections.Concurrent;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.Printers;

public sealed class PrinterVersionCache(
    IMemoryCache cache,
    IOptions<PrinterVersionCacheOptions> options,
    IPrintersService printersService,
    IBackendClientFactory backendClientFactory) : IPrinterVersionCache
{
    private readonly IMemoryCache _cache = cache;
    private readonly PrinterVersionCacheOptions _options = options.Value;
    private readonly IPrintersService _printersService = printersService;
    private readonly IBackendClientFactory _backendClientFactory = backendClientFactory;

    // Throttles repeated explicit refreshes for the same printer so that hammering the
    // "Refresh version info" action cannot force unbounded live backend round-trips; the
    // normal cache TTL already rate-limits automatic polling, but forceRefresh intentionally
    // bypasses that, so it needs its own short, independent minimum interval.
    private static readonly TimeSpan ForceRefreshMinInterval = TimeSpan.FromSeconds(5);

    // Tracks the currently-active forceRefresh throttle window per printer. This must be a
    // single process-wide table (not per-request/per-scope state) so concurrent requests for
    // the same printer actually contend with each other. A plain IMemoryCache
    // TryGetValue-then-Set pair is NOT atomic — two concurrent forceRefresh calls can both
    // observe "no throttle entry yet" before either writes one, defeating the throttle
    // entirely. ConcurrentDictionary.AddOrUpdate performs the read-decide-write as a single
    // atomic operation per key, and a unique per-attempt token (rather than comparing
    // timestamps) makes the "did I win the race" check exact even if two attempts computed
    // the same expiry instant.
    //
    // An entry is only ever claimed for a printer id that FindByIdAsync has already confirmed
    // to exist (see below), and every claim attempt opportunistically sweeps out expired
    // entries first, so this table cannot be grown unboundedly by requests against
    // nonexistent/random printer ids — it is bounded by the number of distinct real printers
    // that have ever been force-refreshed, and expired windows are reclaimed promptly.
    private static readonly ConcurrentDictionary<Guid, (Guid Token, DateTime ExpiresAtUtc)> ForceRefreshWindows = new();

    private static string Key(Guid printerId) => $"printer:version:{printerId:N}";

    // Test-only synchronization seam invoked as the last statement before the atomic
    // AddOrUpdate throttle claim below — after nowUtc/the sweep/myWindow are already computed,
    // with nothing but the atomic call itself remaining. Gating two racing threads at an
    // earlier point (even one statement earlier) only guarantees they are *released* together
    // — it does not guarantee they reach AddOrUpdate at the same instant, so a test built that
    // way could still pass against a non-atomic implementation depending on scheduler luck.
    // This hook lets a test put both threads on a barrier at that exact boundary, closing the
    // gap. It is null (a no-op) in production and must never be set outside a test.
    internal static Action<Guid>? TestOnlyBeforeThrottleClaim { get; set; }

    public async Task<PrinterVersionInfoDto?> GetAsync(Guid printerId, CancellationToken ct, bool forceRefresh = false)
    {
        // Automatic polling (forceRefresh=false) never needs the printer lookup or the
        // throttle table when the normal cache already has a value — keep that fast path
        // exactly as before.
        if (!forceRefresh && _cache.TryGetValue(Key(printerId), out PrinterVersionInfoDto? cachedBeforeLookup) && cachedBeforeLookup is not null)
        {
            return cachedBeforeLookup;
        }

        Printer? printer = await _printersService.FindByIdAsync(printerId, ct);
        if (printer == null)
        {
            return null;
        }

        // An explicit operator-initiated refresh (forceRefresh=true) must bypass any cached
        // result — including a cached partial result recorded while a transient backend fault
        // (e.g. Klippy unavailable) was active — so it can observe recovery immediately instead
        // of waiting out the normal cache TTL. Automatic polling always passes forceRefresh=false
        // and keeps the normal cache policy below untouched. The throttle claim only happens
        // here, after the printer is confirmed to exist, so requests for nonexistent/random
        // printer ids can never grow the throttle table.
        if (forceRefresh)
        {
            DateTime nowUtc = DateTime.UtcNow;
            SweepExpiredForceRefreshWindows(nowUtc);

            (Guid Token, DateTime ExpiresAtUtc) myWindow = (Guid.NewGuid(), nowUtc.Add(ForceRefreshMinInterval));

            // Invoked as the very last statement before the atomic claim itself, after every
            // other per-attempt value (nowUtc, the sweep, myWindow) is already computed, so a
            // test gating both threads here has nothing but the AddOrUpdate call left between
            // release and contention. Firing this any earlier (e.g. before nowUtc/sweep/myWindow)
            // reintroduces a scheduler gap where one thread could race ahead through those steps
            // and complete the claim before the other even attempts it, letting a non-atomic
            // implementation slip through undetected.
            TestOnlyBeforeThrottleClaim?.Invoke(printerId);

            (Guid Token, DateTime ExpiresAtUtc) activeWindow = ForceRefreshWindows.AddOrUpdate(
                printerId,
                myWindow,
                (_, existing) => existing.ExpiresAtUtc > nowUtc ? existing : myWindow);

            if (activeWindow.Token != myWindow.Token)
            {
                // Another forceRefresh call already holds an active throttle window for this
                // printer; fall back to the normal cache-read behavior instead of forcing
                // another live call.
                forceRefresh = false;
            }
        }

        if (!forceRefresh && _cache.TryGetValue(Key(printerId), out PrinterVersionInfoDto? cached) && cached is not null)
        {
            return cached;
        }

        PrinterBackend backend = (PrinterBackend)printer.Backend;
        IBackendClient client = _backendClientFactory.GetClient(backend);

        if (client is not ISupportsPrinterInformation infoClient)
        {
            var unsupported = new PrinterVersionInfoDto(
                PrinterId: printer.Id,
                Backend: backend,
                Supported: false,
                FirmwareVersion: null,
                BackendVersion: null,
                ApiVersion: null,
                RetrievedAtUtc: DateTime.UtcNow,
                Message: "Backend does not support version/firmware information");

            _ = _cache.Set(Key(printerId), unsupported, _options.Ttl);
            return unsupported;
        }

        PrinterVersionInfoDto dto = backend == PrinterBackend.Moonraker
            ? await GetMoonrakerVersionAsync(printer, backend, infoClient, ct)
            : await GetThinProbeVersionAsync(printer, backend, infoClient, ct);

        // A non-null Message on a Supported DTO means the live probe failed (see the catch
        // blocks in GetMoonrakerVersionAsync/GetThinProbeVersionAsync) — cache that outcome only
        // briefly so a transient failure can't keep serving a stale failure message/versions, or
        // block a manual refresh, for the full success TTL.
        bool isFailure = dto.Message is not null;
        _ = _cache.Set(Key(printerId), dto, dto.Supported && dto.FirmwareVersion is not null && !isFailure ? _options.Ttl : TimeSpan.FromSeconds(30));
        return dto;
    }

    /// <summary>
    /// Read-through path for Moonraker/Klipper printers (#1656): the persisted
    /// <c>Printer.Firmware*</c> columns are the single authoritative store, so the version
    /// endpoint reports exactly what <c>PrinterCalibrationContextService.ValidateFirmware</c>
    /// reads — it can never show a firmware identity the calibration gate considers missing.
    /// A live probe of the physical printer runs on every cache miss, exactly as it always has
    /// (this is unchanged from the pre-#1656 thin-probe behavior, so no new printer traffic is
    /// introduced), and still supplies <c>BackendVersion</c>/<c>ApiVersion</c> for display.
    /// Persisting the probed firmware identity to the database — the part of this flow that can
    /// mutate state and therefore needs throttling — is gated by
    /// <see cref="IPrintersService.IsFirmwareReprobeDue"/>, reusing the same
    /// <c>Discovery:FirmwareReprobeIntervalHours</c> cadence guard that already governs discovery
    /// writes, so this hot read path cannot write the database more often than that cadence
    /// allows.
    /// </summary>
    private async Task<PrinterVersionInfoDto> GetMoonrakerVersionAsync(
        Printer printer,
        PrinterBackend backend,
        ISupportsPrinterInformation infoClient,
        CancellationToken ct)
    {
        string? message = null;
        string? liveBackendVersion = null;
        string? liveApiVersion = null;

        try
        {
            StandardPrinterInfo info = await infoClient.GetPrinterInformationAsync(printer.BackendUrl, printer.Credential, ct);
            string? firmware = string.IsNullOrWhiteSpace(info.Firmware) ? null : info.Firmware;
            liveBackendVersion = string.IsNullOrWhiteSpace(info.BackendVersion) ? null : info.BackendVersion;
            liveApiVersion = string.IsNullOrWhiteSpace(info.ApiVersion) ? null : info.ApiVersion;

            if (firmware is not null && _printersService.IsFirmwareReprobeDue(printer))
            {
                // The thin probe (StandardPrinterInfo) only carries the firmware version
                // string. Family/dialect/detection-source/confidence/detection-version are
                // supplied as known constants here (not re-derived via the full
                // MoonrakerOnboardingResolver candidate scan) because the backend type is
                // already registered as Moonraker, not merely guessed during discovery.
                var discovered = new DiscoveredPrinterDto
                {
                    Name = printer.Name,
                    ServerUrl = printer.BackendUrl,
                    Backend = backend,
                    FirmwareFamily = PrinterFirmwareFamily.Klipper,
                    GcodeDialect = PrinterGcodeDialect.Klipper,
                    FirmwareDetectionSource = Domain.FirmwareDetectionSource.Printer,
                    FirmwareVersion = firmware,
                    FirmwareDetectionVersion = MoonrakerOnboardingResolver.FirmwareProbeVersion,
                    FirmwareDetectionConfidence = MoonrakerOnboardingResolver.MapConfidenceScore(100),
                    FirmwareDetectedAtUtc = DateTime.UtcNow,
                };

                _ = await _printersService.RefreshDetectedFirmwareIdentityAsync(printer.Id, discovered, ct);

                // #1656 (Vasquez, PR #1660 review round 2): FindByIdAsync alone cannot be a
                // genuine re-read here — this `printer` instance is already tracked in this
                // scope's DbContext, and EF Core's identity map returns that same in-memory
                // object for a repeat lookup by key without ever re-querying the database. If a
                // concurrent request in a different scope committed a competing firmware write
                // for this printer, this call would otherwise just hand back our own
                // now-possibly-superseded values. The actual fix lives inside
                // RefreshDetectedFirmwareIdentityAsync, which always performs an explicit
                // database reload of this same tracked entity (on every return path, including
                // "declined — lost the cadence race") before returning, so by the time we reach
                // this line `printer` already reflects whatever is truly persisted. This call is
                // kept only as defense in depth for the case where the printer row itself was
                // deleted concurrently.
                //
                // #1656 / PR #1660 review round 8 (Bishop, blocking): a concurrent mid-refresh
                // delete is exactly the case the "defense in depth" comment above assumed
                // FindByIdAsync would already handle by returning null — but FindByIdAsync is
                // backed by DbSet.FindAsync, which is satisfied from the identity map, so it
                // would keep returning this same stale tracked `printer` instance instead of
                // null. RefreshDetectedFirmwareIdentityAsync's own WasFirmwareIdentityPrinterDeletedAsync
                // helper now detaches the tracked entity from this DbContext the moment it
                // confirms the row is genuinely gone, so this FindByIdAsync call is a real,
                // database-backed re-query in that case and correctly returns null (falling back
                // to `printer` here only as a last-resort in-memory snapshot for response shaping
                // — never as a substitute for observing the deletion in RecordedFirmwareIdentity
                // below, which is computed from the persisted columns).
                printer = await _printersService.FindByIdAsync(printer.Id, ct) ?? printer;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never surface a raw exception message to API clients here: unlike the
            // thin-probe path (which only ever talks HTTP to the printer), this path also
            // touches the database via RefreshDetectedFirmwareIdentityAsync, so an
            // unexpected exception type could otherwise leak connection details. Network
            // failures reaching the printer are safe to relay verbatim; anything else is
            // reduced to a generic message.
            message = ex is HttpRequestException or TimeoutException
                ? ex.Message
                : "Firmware re-probe failed; showing last recorded firmware identity.";
        }

        return new PrinterVersionInfoDto(
            PrinterId: printer.Id,
            Backend: backend,
            Supported: true,
            FirmwareVersion: printer.FirmwareVersion,
            BackendVersion: liveBackendVersion ?? printer.BackendVersion,
            ApiVersion: liveApiVersion ?? printer.BackendApiVersion,
            RetrievedAtUtc: DateTime.UtcNow,
            Message: message,
            // #1656, PR #1660 review round 5 (Hicks, blocking): a never-probed printer whose
            // very first live probe attempt fails still has FirmwareFamily == Unknown and
            // FirmwareVersion == null — FromPrinter(printer) unconditionally would still build a
            // non-null CalibrationFirmwareIdentityDto (with Family="Unknown", Version=null),
            // which the UI renders as "Recorded — used for calibration eligibility" even though
            // the calibration gate reports firmware.family and firmware.version as missing for
            // that same printer. FromPrinterIfRecorded suppresses the recorded identity entirely
            // until it is semantically complete, so the two read paths can never disagree about
            // whether an identity has been recorded at all.
            RecordedFirmwareIdentity: CalibrationFirmwareIdentityDto.FromPrinterIfRecorded(printer));
    }

    /// <summary>
    /// Thin-probe path for non-Moonraker backends (PrusaLink, OctoPrint, SDCP, #1656 constraint
    /// #2): <see cref="MoonrakerOnboardingResolver"/> is Moonraker-specific and the calibration
    /// gate only ever accepts Klipper-family firmware, so these backends can never satisfy it
    /// regardless of the value shown here. Behavior is unchanged from before #1656: probe on
    /// every cache miss, no DB persistence attempt, no recorded identity to report.
    /// </summary>
    private static async Task<PrinterVersionInfoDto> GetThinProbeVersionAsync(
        Printer printer,
        PrinterBackend backend,
        ISupportsPrinterInformation infoClient,
        CancellationToken ct)
    {
        try
        {
            StandardPrinterInfo info = await infoClient.GetPrinterInformationAsync(printer.BackendUrl, printer.Credential, ct);

            string? firmware = string.IsNullOrWhiteSpace(info.Firmware) ? null : info.Firmware;
            string? backendVersion = string.IsNullOrWhiteSpace(info.BackendVersion) ? null : info.BackendVersion;
            string? apiVersion = string.IsNullOrWhiteSpace(info.ApiVersion) ? null : info.ApiVersion;

            return new PrinterVersionInfoDto(
                PrinterId: printer.Id,
                Backend: backend,
                Supported: true,
                FirmwareVersion: firmware,
                BackendVersion: backendVersion,
                ApiVersion: apiVersion,
                RetrievedAtUtc: DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PrinterVersionInfoDto(
                PrinterId: printer.Id,
                Backend: backend,
                Supported: true,
                FirmwareVersion: null,
                BackendVersion: null,
                ApiVersion: null,
                RetrievedAtUtc: DateTime.UtcNow,
                Message: ex.Message);
        }
    }

    // Opportunistically reclaims expired throttle windows so the table cannot grow without
    // bound across the lifetime of the process. This runs on every forceRefresh attempt
    // (a low-frequency, operator-initiated path, so the O(n) scan cost is negligible), and
    // uses the conditional KeyValuePair-based TryRemove overload so a window that has just
    // been refreshed by a concurrent claim (i.e. no longer matches the expired snapshot we
    // observed) is never removed out from under it.
    private static void SweepExpiredForceRefreshWindows(DateTime nowUtc)
    {
        foreach (KeyValuePair<Guid, (Guid Token, DateTime ExpiresAtUtc)> entry in ForceRefreshWindows.Where(e => e.Value.ExpiresAtUtc <= nowUtc))
        {
            _ = ForceRefreshWindows.TryRemove(entry);
        }
    }
}
