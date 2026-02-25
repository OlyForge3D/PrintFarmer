using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Lightweight unauthenticated API for FilamentManager (ESP32 NFC firmware).
/// Returns minimal printer data so the firmware can display a printer list
/// without needing full PrintFarmer credentials.
/// </summary>
[ApiController]
[Route("api/filaman")]
[Tags("FilamentManager")]
public class FilaManController(IPrintersService printersService) : ControllerBase
{
    /// <summary>
    /// Returns a minimal list of enabled printers (id, name, backend).
    /// Called by FilamentManager firmware to populate its printer selector.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("printers")]
    [ProducesResponseType(typeof(FilaManPrinterDto[]), 200)]
    public async Task<ActionResult<FilaManPrinterDto[]>> GetPrintersAsync(CancellationToken ct)
    {
        List<Printer> printers = await printersService.GetAllAsync(ct);

        FilaManPrinterDto[] result = printers
            .Where(p => p.IsEnabled)
            .Select(p => new FilaManPrinterDto(
                p.Id.ToString(),
                p.Name,
                p.Backend.ToString()))
            .ToArray();

        return Ok(result);
    }
}

/// <summary>
/// Minimal printer info returned to FilamentManager firmware.
/// </summary>
public record FilaManPrinterDto(string Id, string Name, string Backend);
