using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Shared implementation for IManagedSpoolProvider that fetches spool details
/// from PrintFarmer's database + central Spoolman instance.
/// Injected into non-Moonraker status clients to avoid duplicating spool resolution logic.
/// </summary>
public class ManagedSpoolProviderHelper(ISpoolmanService spoolmanService, ILogger<ManagedSpoolProviderHelper> logger)
{
    private readonly ISpoolmanService _spoolmanService = spoolmanService;
    private readonly ILogger<ManagedSpoolProviderHelper> _logger = logger;

    /// <summary>
    /// Resolves spool info from the printer's CurrentSpoolId using the central Spoolman instance.
    /// </summary>
    public async Task<PrinterSpoolInfoDto?> GetManagedSpoolInfoAsync(Printer printer, CancellationToken ct)
    {
        if (printer.CurrentSpoolId is not { } spoolId)
        {
            return null;
        }

        try
        {
            SpoolmanSpoolDto? spool = await _spoolmanService.GetSpoolByIdAsync(spoolId, ct).ConfigureAwait(false);
            if (spool is null)
            {
                _logger.LogDebug("Spool {SpoolId} not found in Spoolman for printer {PrinterId}", spoolId, printer.Id);
                return new PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: spoolId);
            }

            return new PrinterSpoolInfoDto(
                HasActiveSpool: true,
                ActiveSpoolId: spoolId,
                SpoolName: spool.FilamentName,
                Material: spool.Material,
                ColorHex: spool.ColorHex != null ? (spool.ColorHex.StartsWith('#') ? spool.ColorHex : $"#{spool.ColorHex}") : null,
                FilamentName: spool.FilamentName,
                Vendor: spool.Vendor,
                RemainingWeightG: spool.RemainingWeightG);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch spool {SpoolId} from Spoolman for printer {PrinterId}", spoolId, printer.Id);
            return new PrinterSpoolInfoDto(HasActiveSpool: true, ActiveSpoolId: spoolId);
        }
    }
}
