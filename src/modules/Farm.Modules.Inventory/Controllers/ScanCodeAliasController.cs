using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Inventory.Controllers;

/// <summary>Request to remember a non-GTIN scan code for a filament.</summary>
public record ScanCodeAliasRequest
{
    public string? Code { get; init; }

    public int? FilamentId { get; init; }
}

/// <summary>A remembered scan code and the filament it resolves to.</summary>
public record ScanCodeAliasDto(
    int Id,
    string Code,
    int FilamentId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    SpoolmanFilamentDto? Filament);

/// <summary>Outcome of looking up a scanned code.</summary>
public enum ScanCodeAliasLookupState
{
    /// <summary>The code is remembered and its filament exists.</summary>
    Found,

    /// <summary>The code has never been remembered.</summary>
    NotRemembered,

    /// <summary>The code is remembered, but its filament no longer exists in Spoolman.</summary>
    FilamentMissing,
}

/// <summary>
/// Lookup response. A miss is a 200 with an explicit state so clients can tell a genuine
/// "not remembered" apart from a missing route (404) on an older server.
/// </summary>
public record ScanCodeAliasLookupResponse(string Code, ScanCodeAliasLookupState State, ScanCodeAliasDto? Alias);

/// <summary>
/// Remembers scanned codes that are not retail GTINs (for example Amazon FNSKU stickers) as
/// exact-match aliases for a Spoolman filament. Aliases are stored in PrintFarmer and are kept
/// separate from the Spoolman <c>filament.gtin</c> barcode mapping.
/// </summary>
[ApiController]
[Route("api/spoolman/code-aliases")]
[Tags("Spoolman Integration")]
[Authorize]
public class ScanCodeAliasController(
    IScanCodeAliasService aliasService,
    IBarcodeScanLogService barcodeScanLogService,
    ILogger<ScanCodeAliasController> logger) : ControllerBase
{
    private const int LogCodeMaxLength = 256;

    /// <summary>Looks up a remembered scan code and its filament.</summary>
    /// <param name="code">Scanned value. It is matched exactly (case-sensitive) after trimming.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The lookup state, with the alias and filament when found.</returns>
    /// <response code="200">Lookup completed; <c>state</c> is found, notRemembered or filamentMissing.</response>
    /// <response code="400">The code is empty, too long, contains control characters, or is a GTIN.</response>
    /// <response code="502">Spoolman could not be reached to load the filament.</response>
    [HttpGet("filament")]
    [ProducesResponseType(typeof(ScanCodeAliasLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ScanCodeAliasLookupResponse>> LookupAsync([FromQuery] string? code, CancellationToken ct)
    {
        string logged = code?.Trim() ?? string.Empty;
        try
        {
            ScanCodeAliasLookupResult result = await aliasService.LookupAsync(code, ct);
            switch (result.Status)
            {
                case ScanCodeAliasLookupStatus.Invalid:
                    return BadRequest(new { message = result.Error });
                case ScanCodeAliasLookupStatus.NotFound:
                    await LogAsync(logged, BarcodeScanAction.AliasResolve, BarcodeScanOutcome.NotFound, StatusCodes.Status200OK, null, "Scan code is not remembered.");
                    return Ok(new ScanCodeAliasLookupResponse(logged, ScanCodeAliasLookupState.NotRemembered, null));
                case ScanCodeAliasLookupStatus.FilamentMissing:
                    await LogAsync(logged, BarcodeScanAction.AliasResolve, BarcodeScanOutcome.NotFound, StatusCodes.Status200OK, result.Alias?.FilamentId, result.Error ?? string.Empty);
                    return Ok(new ScanCodeAliasLookupResponse(logged, ScanCodeAliasLookupState.FilamentMissing, ToDto(result.Alias!, null)));
                default:
                    await LogAsync(logged, BarcodeScanAction.AliasResolve, BarcodeScanOutcome.Resolved, StatusCodes.Status200OK, result.Filament!.Id, "Scan code resolved to filament.");
                    return Ok(new ScanCodeAliasLookupResponse(logged, ScanCodeAliasLookupState.Found, ToDto(result.Alias!, result.Filament)));
            }
        }
        catch (SpoolmanApiException ex)
        {
            logger.LogWarning(ex, "Spoolman failed while resolving scan code {Code}", LogSanitizer.Sanitize(logged));
            await LogAsync(logged, BarcodeScanAction.AliasResolve, BarcodeScanOutcome.Error, StatusCodes.Status502BadGateway, null, "Spoolman could not be reached.");
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "Spoolman could not be reached. Try again." });
        }
    }

    /// <summary>Remembers a non-GTIN scan code for a filament.</summary>
    /// <param name="request">Scanned code and the explicitly chosen filament ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored alias.</returns>
    /// <response code="200">The code was already remembered for this filament.</response>
    /// <response code="201">The code was remembered, or a stale alias was replaced.</response>
    /// <response code="400">The code or filament ID is invalid, or the code is a GTIN.</response>
    /// <response code="404">The filament does not exist in Spoolman.</response>
    /// <response code="409">The code is already remembered for a different filament.</response>
    /// <response code="502">Spoolman could not be reached to confirm the filament.</response>
    [HttpPost]
    [RequirePermission("spoolman", "admin")]
    [ProducesResponseType(typeof(ScanCodeAliasDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ScanCodeAliasDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ScanCodeAliasDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ScanCodeAliasDto>> SaveAsync([FromBody] ScanCodeAliasRequest? request, CancellationToken ct)
    {
        if (request?.FilamentId is not int filamentId)
        {
            return BadRequest(new { message = "A valid filament ID is required." });
        }

        string logged = request.Code?.Trim() ?? string.Empty;
        try
        {
            ScanCodeAliasSaveResult result = await aliasService.SaveAsync(request.Code, filamentId, CurrentUserId(), ct);
            switch (result.Status)
            {
                case ScanCodeAliasSaveStatus.Invalid:
                    return BadRequest(new { message = result.Error });
                case ScanCodeAliasSaveStatus.FilamentNotFound:
                    await LogAsync(logged, BarcodeScanAction.AliasMapping, BarcodeScanOutcome.NotFound, StatusCodes.Status404NotFound, filamentId, result.Error ?? string.Empty);
                    return NotFound(new { message = result.Error });
                case ScanCodeAliasSaveStatus.Conflict:
                    await LogAsync(logged, BarcodeScanAction.AliasMapping, BarcodeScanOutcome.Error, StatusCodes.Status409Conflict, result.Alias?.FilamentId, result.Error ?? string.Empty);
                    return Conflict(ToDto(result.Alias!, result.Filament));
                case ScanCodeAliasSaveStatus.AlreadyMapped:
                    await LogAsync(logged, BarcodeScanAction.AliasMapping, BarcodeScanOutcome.Mapped, StatusCodes.Status200OK, filamentId, "Scan code already remembered for filament.");
                    return Ok(ToDto(result.Alias!, result.Filament));
                default:
                    await LogAsync(logged, BarcodeScanAction.AliasMapping, BarcodeScanOutcome.Mapped, StatusCodes.Status201Created, filamentId, "Scan code remembered for filament.");
                    return StatusCode(StatusCodes.Status201Created, ToDto(result.Alias!, result.Filament));
            }
        }
        catch (SpoolmanApiException ex)
        {
            logger.LogWarning(ex, "Spoolman failed while saving scan code {Code}", LogSanitizer.Sanitize(logged));
            await LogAsync(logged, BarcodeScanAction.AliasMapping, BarcodeScanOutcome.Error, StatusCodes.Status502BadGateway, filamentId, "Spoolman could not be reached.");
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "Spoolman could not be reached. Try again." });
        }
    }

    private static ScanCodeAliasDto ToDto(ScanCodeAlias alias, SpoolmanFilamentDto? filament)
        => new(alias.Id, alias.Code, alias.FilamentId, alias.CreatedAt, alias.UpdatedAt, filament);

    private string? CurrentUserId()
    {
        ClaimsPrincipal? user = HttpContext?.User;
        return user?.FindFirstValue(ClaimTypes.NameIdentifier) ?? user?.FindFirstValue("sub");
    }

    private Task LogAsync(string code, BarcodeScanAction action, BarcodeScanOutcome outcome, int status, int? filamentId, string message)
        => barcodeScanLogService.LogAsync(new BarcodeScanLog
        {
            Timestamp = DateTime.UtcNow,
            Barcode = code.Length > LogCodeMaxLength ? code[..LogCodeMaxLength] : code,
            Action = action,
            Outcome = outcome,
            HttpStatus = status,
            MatchedFilamentId = filamentId,
            UserId = CurrentUserId(),
            Message = message,
        });
}
