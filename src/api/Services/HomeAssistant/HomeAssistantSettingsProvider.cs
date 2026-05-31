using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.HomeAssistant;

/// <summary>
/// Reads Home Assistant connection settings from <see cref="AppDbContext"/>
/// and decrypts the long-lived access token on demand.
/// </summary>
public sealed class HomeAssistantSettingsProvider(
    AppDbContext dbContext,
    ISensitiveDataProtector protector,
    ILogger<HomeAssistantSettingsProvider> logger) : IHomeAssistantSettingsProvider
{
    /// <inheritdoc/>
    public async Task<HomeAssistantConnectionConfig?> GetEnabledConfigAsync(CancellationToken ct)
    {
        Farm.Infrastructure.Domain.HomeAssistantSettings? settings =
            await dbContext.HomeAssistantSettings.FirstOrDefaultAsync(ct);

        if (settings == null || !settings.Enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.BaseUrl) ||
            string.IsNullOrWhiteSpace(settings.LongLivedAccessToken))
        {
            logger.LogWarning("HomeAssistant integration is enabled but BaseUrl or token is not configured");
            return null;
        }

        string? plainToken = protector.Unprotect(settings.LongLivedAccessToken);
        if (string.IsNullOrWhiteSpace(plainToken))
        {
            logger.LogWarning("HomeAssistant token could not be decrypted");
            return null;
        }

        return new HomeAssistantConnectionConfig(settings.BaseUrl, plainToken);
    }
}
