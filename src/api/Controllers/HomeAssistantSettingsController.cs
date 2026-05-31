using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.Security;
using Farm.Web.Api.Services.HomeAssistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// CRUD and connection-test endpoints for the optional Home Assistant integration.
/// All endpoints require the <c>farm_admin</c> role.
/// </summary>
[ApiController]
[Route("api/settings/home-assistant")]
[Authorize]
[Authorize(Roles = "farm_admin")]
public class HomeAssistantSettingsController(
    AppDbContext dbContext,
    ISensitiveDataProtector protector,
    IHomeAssistantClient haClient,
    ILogger<HomeAssistantSettingsController> logger) : ControllerBase
{
    /// <summary>Gets the current Home Assistant integration settings.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(HomeAssistantSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<HomeAssistantSettingsDto>> GetAsync(CancellationToken ct)
    {
        HomeAssistantSettings settings = await GetOrCreateSingletonAsync(ct);
        return Ok(ToDto(settings));
    }

    /// <summary>Updates the Home Assistant integration settings.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(HomeAssistantSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HomeAssistantSettingsDto>> UpdateAsync(
        [FromBody] UpdateHomeAssistantSettingsDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        HomeAssistantSettings settings = await GetOrCreateSingletonAsync(ct);

        // Apply AllowPrivateNetworkTargets first so URL validation uses the new value.
        if (dto.AllowPrivateNetworkTargets.HasValue)
        {
            settings.AllowPrivateNetworkTargets = dto.AllowPrivateNetworkTargets.Value;
        }

        if (dto.BaseUrl != null)
        {
            UrlSsrfValidationResult ssrfResult = UrlSsrfValidator.Validate(
                dto.BaseUrl, settings.AllowPrivateNetworkTargets);
            if (!ssrfResult.IsValid)
            {
                return BadRequest(ssrfResult.ErrorMessage);
            }
        }

        if (dto.Enabled.HasValue)
        {
            settings.Enabled = dto.Enabled.Value;
        }

        if (dto.BaseUrl != null)
        {
            settings.BaseUrl = dto.BaseUrl.TrimEnd('/');
        }

        // Empty string clears the token; null means "no change".
        if (dto.LongLivedAccessToken is not null)
        {
            settings.LongLivedAccessToken = string.IsNullOrWhiteSpace(dto.LongLivedAccessToken)
                ? null
                : dto.LongLivedAccessToken.Trim();
        }

        settings.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("[HomeAssistant] Settings updated — enabled={Enabled}", settings.Enabled);
        return Ok(ToDto(settings));
    }

    /// <summary>
    /// Tests connectivity to Home Assistant using the provided or stored settings.
    /// Returns HA version and entity count on success.
    /// </summary>
    [HttpPost("test")]
    [ProducesResponseType(typeof(HomeAssistantTestResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<HomeAssistantTestResultDto>> TestAsync(
        [FromBody] TestHomeAssistantDto? dto, CancellationToken ct)
    {
        string? baseUrl = dto?.BaseUrl;
        string? token = dto?.LongLivedAccessToken;

        HomeAssistantSettings stored = await GetOrCreateSingletonAsync(ct);

        // Fall back to stored settings when the caller didn't supply values inline.
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = stored.BaseUrl;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                token = string.IsNullOrWhiteSpace(stored.LongLivedAccessToken)
                    ? null
                    : protector.Unprotect(stored.LongLivedAccessToken);
            }
        }

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            return Ok(new HomeAssistantTestResultDto(false, null, null,
                "baseUrl and longLivedAccessToken are required for a connection test."));
        }

        // SSRF validation before making any outbound request
        UrlSsrfValidationResult ssrfResult = UrlSsrfValidator.Validate(
            baseUrl, stored.AllowPrivateNetworkTargets);
        if (!ssrfResult.IsValid)
        {
            return Ok(new HomeAssistantTestResultDto(false, null, null, ssrfResult.ErrorMessage));
        }

        Services.HomeAssistant.HomeAssistantConnectionResult result =
            await haClient.TestConnectionAsync(baseUrl, token, ct);

        return Ok(new HomeAssistantTestResultDto(
            result.Success, result.Version, result.EntityCount, result.ErrorMessage));
    }

    /// <summary>
    /// Lists power-capable entities (switches, power/energy sensors) from Home Assistant.
    /// </summary>
    [HttpGet("entities")]
    [ProducesResponseType(typeof(IReadOnlyList<HomeAssistantEntityInfoDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<HomeAssistantEntityInfoDto>>> GetEntitiesAsync(CancellationToken ct)
    {
        HomeAssistantSettings settings = await GetOrCreateSingletonAsync(ct);

        if (!settings.Enabled ||
            string.IsNullOrWhiteSpace(settings.BaseUrl) ||
            string.IsNullOrWhiteSpace(settings.LongLivedAccessToken))
        {
            return BadRequest("Home Assistant integration is not configured or disabled.");
        }

        string? token = protector.Unprotect(settings.LongLivedAccessToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return BadRequest("Home Assistant token could not be decrypted.");
        }

        IReadOnlyList<Services.HomeAssistant.HomeAssistantEntityInfo> entities =
            await haClient.GetPowerEntitiesAsync(settings.BaseUrl, token, ct);

        return Ok(entities.Select(e => new HomeAssistantEntityInfoDto(
            e.EntityId, e.FriendlyName, e.Domain, e.DeviceClass, e.State)).ToList());
    }

    private async Task<HomeAssistantSettings> GetOrCreateSingletonAsync(CancellationToken ct)
    {
        HomeAssistantSettings? settings = await dbContext.HomeAssistantSettings.FirstOrDefaultAsync(ct);
        if (settings == null)
        {
            settings = new HomeAssistantSettings
            {
                Id = 1,
                Enabled = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            dbContext.HomeAssistantSettings.Add(settings);
            await dbContext.SaveChangesAsync(ct);
        }

        return settings;
    }

    private static HomeAssistantSettingsDto ToDto(HomeAssistantSettings s) => new(
        s.Enabled,
        s.BaseUrl,
        HasToken: !string.IsNullOrWhiteSpace(s.LongLivedAccessToken),
        s.AllowPrivateNetworkTargets,
        s.UpdatedAt);
}

/// <summary>DTO returned for Home Assistant settings reads.</summary>
public sealed record HomeAssistantSettingsDto(
    bool Enabled,
    string? BaseUrl,
    bool HasToken,
    bool AllowPrivateNetworkTargets,
    DateTime UpdatedAt);

/// <summary>DTO for updating Home Assistant settings. Null fields are not changed.</summary>
public sealed class UpdateHomeAssistantSettingsDto
{
    public bool? Enabled { get; init; }

    [MaxLength(500)]
    public string? BaseUrl { get; init; }

    /// <summary>Set to empty string to clear the token; null to leave unchanged.</summary>
    [MaxLength(2000)]
    public string? LongLivedAccessToken { get; init; }

    /// <summary>
    /// When true, allows private/internal network targets (RFC1918, unique-local IPv6).
    /// Required for typical Home Assistant deployments on a home LAN.
    /// </summary>
    public bool? AllowPrivateNetworkTargets { get; init; }
}

/// <summary>Optional inline credentials for the test endpoint.</summary>
public sealed class TestHomeAssistantDto
{
    [MaxLength(500)]
    public string? BaseUrl { get; init; }

    [MaxLength(2000)]
    public string? LongLivedAccessToken { get; init; }
}

/// <summary>Result of a Home Assistant connectivity test.</summary>
public sealed record HomeAssistantTestResultDto(
    bool Success,
    string? Version,
    int? EntityCount,
    string? ErrorMessage);

/// <summary>DTO for a Home Assistant entity.</summary>
public sealed record HomeAssistantEntityInfoDto(
    string EntityId,
    string FriendlyName,
    string Domain,
    string? DeviceClass,
    string State);
