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
