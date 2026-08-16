using System.Collections.Concurrent;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
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
    private static readonly ConcurrentDictionary<Guid, (Guid Token, DateTime ExpiresAtUtc)> ForceRefreshWindows = new();

    private static string Key(Guid printerId) => $"printer:version:{printerId:N}";

    public async Task<PrinterVersionInfoDto?> GetAsync(Guid printerId, CancellationToken ct, bool forceRefresh = false)
    {
        // An explicit operator-initiated refresh (forceRefresh=true) must bypass any cached
        // result — including a cached partial result recorded while a transient backend fault
        // (e.g. Klippy unavailable) was active — so it can observe recovery immediately instead
        // of waiting out the normal cache TTL. Automatic polling always passes forceRefresh=false
        // and keeps the normal cache policy below untouched.
        if (forceRefresh)
        {
            DateTime nowUtc = DateTime.UtcNow;
            (Guid Token, DateTime ExpiresAtUtc) myWindow = (Guid.NewGuid(), nowUtc.Add(ForceRefreshMinInterval));

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

        Printer? printer = await _printersService.FindByIdAsync(printerId, ct);
        if (printer == null)
        {
            return null;
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

        try
        {
            StandardPrinterInfo info = await infoClient.GetPrinterInformationAsync(printer.BackendUrl, printer.Credential, ct);

            string? firmware = string.IsNullOrWhiteSpace(info.Firmware) ? null : info.Firmware;
            string? backendVersion = string.IsNullOrWhiteSpace(info.BackendVersion) ? null : info.BackendVersion;
            string? apiVersion = string.IsNullOrWhiteSpace(info.ApiVersion) ? null : info.ApiVersion;

            var dto = new PrinterVersionInfoDto(
                PrinterId: printer.Id,
                Backend: backend,
                Supported: true,
                FirmwareVersion: firmware,
                BackendVersion: backendVersion,
                ApiVersion: apiVersion,
                RetrievedAtUtc: DateTime.UtcNow);

            _ = _cache.Set(Key(printerId), dto, _options.Ttl);
            return dto;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var dto = new PrinterVersionInfoDto(
                PrinterId: printer.Id,
                Backend: backend,
                Supported: true,
                FirmwareVersion: null,
                BackendVersion: null,
                ApiVersion: null,
                RetrievedAtUtc: DateTime.UtcNow,
                Message: ex.Message);

            _ = _cache.Set(Key(printerId), dto, TimeSpan.FromSeconds(30));
            return dto;
        }
    }
}
