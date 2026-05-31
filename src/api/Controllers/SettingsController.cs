using System.Security.Claims;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

        return Ok(new FarmSettingsResponse(
            ElectricityRatePerKwh: dto.ElectricityRatePerKwh,
            DefaultMachineHourlyRate: dto.DefaultMachineHourlyRate,
            AveragePrinterWattage: dto.AveragePrinterWattage,
            CanWrite: isAdmin));
    }

    /// <summary>Updates farm-wide settings. Requires farm_admin role.</summary>
    [HttpPut("farm")]
    [Authorize(Policy = "RequireAdmin")]
    [ProducesResponseType(typeof(FarmSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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

        _farmSettings.UpdateFarmSettings(new UpdateFarmSettingsRequest(
            body.ElectricityRatePerKwh,
            body.DefaultMachineHourlyRate,
            body.AveragePrinterWattage));

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
    public async Task<IActionResult> UpdateUserSettingsAsync(
        [FromBody] UpdateUserSettingsBody body, CancellationToken ct)
    {
        if (body.ItemsPerPage is < 1 or > 200)
        {
            return BadRequest("itemsPerPage must be between 1 and 200.");
        }

        Guid userId = GetUserId();
        UserSettings? entity = await _db.UserSettings.FirstOrDefaultAsync(u => u.UserId == userId, ct);

        if (entity is null)
        {
            entity = new UserSettings { UserId = userId };
            _db.UserSettings.Add(entity);
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

        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

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
            DefaultSlicerPreset: entity?.DefaultSlicerPreset);
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

/// <summary>Response body for farm-wide settings endpoints.</summary>
public record FarmSettingsResponse(
    decimal ElectricityRatePerKwh,
    decimal DefaultMachineHourlyRate,
    decimal AveragePrinterWattage,
    bool CanWrite);

/// <summary>Request body for PUT /api/settings/farm.</summary>
public record UpdateFarmSettingsBody(
    decimal? ElectricityRatePerKwh,
    decimal? DefaultMachineHourlyRate,
    decimal? AveragePrinterWattage);

/// <summary>Response body for user settings endpoints.</summary>
public record UserSettingsResponse(
    Guid UserId,
    string Theme,
    string Locale,
    int ItemsPerPage,
    string? DefaultSlicerPreset);

/// <summary>Request body for PUT /api/settings/user. All fields optional (partial update).</summary>
public record UpdateUserSettingsBody(
    string? Theme,
    string? Locale,
    int? ItemsPerPage,
    string? DefaultSlicerPreset);
