using System.Security.Claims;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages the per-user vs farm-wide settings split.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>GET  /api/settings/farm</c> — all authenticated users; <c>canWrite</c> is true only for admins.</item>
/// <item><c>PUT  /api/settings/farm</c> — admin role required.</item>
/// <item><c>GET  /api/settings/user</c> — current user's settings.</item>
/// <item><c>PUT  /api/settings/user</c> — current user updates own settings.</item>
/// </list>
/// </remarks>
[ApiController]
[Route("api/settings")]
[Authorize]
public class SettingsController(
    IFarmSettingsService farmSettingsService,
    AppDbContext db,
    ILogger<SettingsController> logger) : ControllerBase
{
    private readonly IFarmSettingsService _farmSettings = farmSettingsService;
    private readonly AppDbContext _db = db;
    private readonly ILogger<SettingsController> _logger = logger;

    // ─── Farm settings ───────────────────────────────────────────────────────

    /// <summary>Gets farm-wide settings. All users can read; canWrite is true only for admins.</summary>
    [HttpGet("farm")]
    [ProducesResponseType(typeof(FarmSettingsResponse), StatusCodes.Status200OK)]
    public IActionResult GetFarmSettings()
    {
        bool isAdmin = User.IsInRole("farm_admin");
        FarmSettingsDto dto = _farmSettings.GetFarmSettings();

        // Include the row version from the underlying AppSettingsEntity for concurrency control
        string? rowVersion = _farmSettings.GetFarmSettingsRowVersion();

        return Ok(new FarmSettingsResponse(
            ElectricityRatePerKwh: dto.ElectricityRatePerKwh,
            DefaultMachineHourlyRate: dto.DefaultMachineHourlyRate,
            AveragePrinterWattage: dto.AveragePrinterWattage,
            CanWrite: isAdmin,
            RowVersion: rowVersion));
    }

    /// <summary>Updates farm-wide settings. Requires farm_admin role.</summary>
    [HttpPut("farm")]
    [Authorize(Policy = "RequireAdmin")]
    [ProducesResponseType(typeof(FarmSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult UpdateFarmSettings([FromBody] UpdateFarmSettingsBody body)
    {
        if (body.ElectricityRatePerKwh is < 0 or > 10)
        {
            return BadRequest("electricityRatePerKwh must be between 0 and 10.");
        }

        if (body.DefaultMachineHourlyRate is < 0 or > 100)
        {
            return BadRequest("defaultMachineHourlyRate must be between 0 and 100.");
        }

        if (body.AveragePrinterWattage is < 0 or > 5000)
        {
            return BadRequest("averagePrinterWattage must be between 0 and 5000.");
        }

        string? existingRowVersion = _farmSettings.GetFarmSettingsRowVersion();
        string? expectedRowVersion = null;
        if (!string.IsNullOrWhiteSpace(existingRowVersion))
        {
            IActionResult? tokenValidationError = TryGetValidatedConcurrencyToken(
                body.RowVersion,
                out string validatedRowVersion,
                out _);
            if (tokenValidationError is not null)
            {
                return tokenValidationError;
            }

            expectedRowVersion = validatedRowVersion;
        }

        try
        {
            _farmSettings.UpdateFarmSettings(
                new UpdateFarmSettingsRequest(
                    body.ElectricityRatePerKwh,
                    body.DefaultMachineHourlyRate,
                    body.AveragePrinterWattage),
                expectedRowVersion);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "The farm settings were modified by another request. Please reload and retry." });
        }

        _logger.LogInformation("Farm settings updated by user {UserId}", GetUserId());

        return GetFarmSettings();
    }

    // ─── User settings ────────────────────────────────────────────────────────

    /// <summary>Gets the current user's personal settings.</summary>
    [HttpGet("user")]
    [ProducesResponseType(typeof(UserSettingsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUserSettingsAsync(CancellationToken ct)
    {
        Guid userId = GetUserId();
        UserSettings? entity = await _db.UserSettings.FirstOrDefaultAsync(u => u.UserId == userId, ct);

        return Ok(ToResponse(entity, userId));
    }

    /// <summary>Updates the current user's personal settings.</summary>
    [HttpPut("user")]
    [ProducesResponseType(typeof(UserSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateUserSettingsAsync(
        [FromBody] UpdateUserSettingsBody body, CancellationToken ct)
    {
        if (body.ItemsPerPage is < 1 or > 200)
        {
            return BadRequest("itemsPerPage must be between 1 and 200.");
        }

        if (body.PrintablesUsername is { Length: > 64 })
        {
            return BadRequest("printablesUsername must be 64 characters or fewer.");
        }

        string? normalizedPrintablesUsername = string.IsNullOrWhiteSpace(body.PrintablesUsername)
            ? null
            : body.PrintablesUsername.Trim();

        if (normalizedPrintablesUsername is not null && normalizedPrintablesUsername.StartsWith('@'))
        {
            return BadRequest("printablesUsername must not start with '@'.");
        }

        Guid userId = GetUserId();
        UserSettings? entity = await _db.UserSettings.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        byte[]? expectedRowVersionBytes = null;
        if (entity is not null)
        {
            IActionResult? tokenValidationError = TryGetValidatedConcurrencyToken(
                body.RowVersion,
                out _,
                out byte[] validatedRowVersionBytes);
            if (tokenValidationError is not null)
            {
                return tokenValidationError;
            }

            expectedRowVersionBytes = validatedRowVersionBytes;
        }

        if (entity is null)
        {
            entity = new UserSettings { UserId = userId };
            _db.UserSettings.Add(entity);
        }
        else
        {
            // Enforce optimistic concurrency: set the original row version so EF checks it
            _db.Entry(entity).Property(e => e.RowVersion).OriginalValue = expectedRowVersionBytes!;
        }

        if (body.Theme is not null)
        {
            entity.Theme = body.Theme;
        }

        if (body.Locale is not null)
        {
            entity.Locale = body.Locale;
        }

        if (body.ItemsPerPage.HasValue)
        {
            entity.ItemsPerPage = body.ItemsPerPage.Value;
        }

        if (body.DefaultSlicerPreset is not null)
        {
            entity.DefaultSlicerPreset = body.DefaultSlicerPreset;
        }

        if (body.PrintablesUsername is not null)
        {
            entity.PrintablesUsername = normalizedPrintablesUsername;
        }

        entity.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "User settings were modified by another request. Please reload and retry." });
        }

        _logger.LogInformation("User settings updated for user {UserId}", userId);

        return Ok(ToResponse(entity, userId));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────
    private Guid GetUserId()
    {
        string? raw = User?.FindFirstValue("sub")
            ?? User?.FindFirstValue(ClaimTypes.NameIdentifier);

        return string.IsNullOrEmpty(raw) || !Guid.TryParse(raw, out Guid id)
            ? throw new InvalidOperationException("User ID not found in claims.")
            : id;
    }

    private static UserSettingsResponse ToResponse(UserSettings? entity, Guid userId) =>
        new(
            UserId: userId,
            Theme: entity?.Theme ?? "system",
            Locale: entity?.Locale ?? "en",
            ItemsPerPage: entity?.ItemsPerPage ?? 25,
            DefaultSlicerPreset: entity?.DefaultSlicerPreset,
            RowVersion: entity?.RowVersion is { Length: > 0 } rv ? Convert.ToBase64String(rv) : null,
            PrintablesUsername: entity?.PrintablesUsername);

    private IActionResult? TryGetValidatedConcurrencyToken(
        string? bodyRowVersion,
        out string expectedRowVersion,
        out byte[] expectedRowVersionBytes)
    {
        expectedRowVersion = string.Empty;
        expectedRowVersionBytes = [];

        string? ifMatch = Request.Headers.IfMatch.FirstOrDefault()?.Trim().Trim('"');
        string? token = string.IsNullOrWhiteSpace(ifMatch) ? bodyRowVersion : ifMatch;

        if (string.IsNullOrWhiteSpace(token))
        {
            return StatusCode(
                StatusCodes.Status428PreconditionRequired,
                new { message = "Missing concurrency token. Provide If-Match header or rowVersion." });
        }

        try
        {
            expectedRowVersionBytes = Convert.FromBase64String(token);
            expectedRowVersion = token;
            return null;
        }
        catch (FormatException)
        {
            return BadRequest(new { message = "Invalid concurrency token. If-Match/rowVersion must be valid base64." });
        }
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

/// <summary>Response body for farm-wide settings endpoints.</summary>
public record FarmSettingsResponse(
    decimal ElectricityRatePerKwh,
    decimal DefaultMachineHourlyRate,
    decimal AveragePrinterWattage,
    bool CanWrite,
    string? RowVersion);

/// <summary>Request body for PUT /api/settings/farm.</summary>
public record UpdateFarmSettingsBody(
    decimal? ElectricityRatePerKwh,
    decimal? DefaultMachineHourlyRate,
    decimal? AveragePrinterWattage,
    string? RowVersion = null);

/// <summary>Response body for user settings endpoints.</summary>
public record UserSettingsResponse(
    Guid UserId,
    string Theme,
    string Locale,
    int ItemsPerPage,
    string? DefaultSlicerPreset,
    string? RowVersion,
    string? PrintablesUsername = null);

/// <summary>Request body for PUT /api/settings/user. All fields optional (partial update).</summary>
public record UpdateUserSettingsBody(
    string? Theme,
    string? Locale,
    int? ItemsPerPage,
    string? DefaultSlicerPreset,
    string? RowVersion = null,
    string? PrintablesUsername = null);
