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

    private static string Key(Guid printerId) => $"printer:version:{printerId:N}";

    public async Task<PrinterVersionInfoDto?> GetAsync(Guid printerId, CancellationToken ct)
    {
        if (_cache.TryGetValue(Key(printerId), out PrinterVersionInfoDto? cached) && cached is not null)
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

                // Re-read so the response reflects exactly what was persisted (defense in
                // depth: RefreshDetectedFirmwareIdentityAsync re-checks the cadence guard
                // itself and may have declined to write if it lost a race).
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
            RecordedFirmwareIdentity: CalibrationFirmwareIdentityDto.FromPrinter(printer));
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
}
