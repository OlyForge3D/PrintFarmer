using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Persisted settings for the optional Home Assistant smart plug integration.
/// The long-lived access token is stored encrypted (via ISensitiveDataProtector)
/// in <see cref="EncryptedToken"/>; it is never returned in plain form to API clients.
/// </summary>
[AppSetting(SectionName)]
[SettingGroup("Integrations", DisplayName = "Integrations", Description = "External service integrations", Icon = "pf-icon-integration", Order = 5)]
[SettingDisplay(Name = "Home Assistant", Description = "Settings for optional Home Assistant smart plug integration.", Icon = "pf-icon-homeassistant", Group = "Integrations", Order = 2)]
public class HomeAssistantSettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "HomeAssistant";

    public static string SectionKey => SectionName;

    [SettingDisplay(
        Name = "Enabled",
        Description = "Enable Home Assistant smart plug integration.",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [SettingDisplay(
        Name = "Base URL",
        Description = "Home Assistant base URL (e.g., http://homeassistant.local:8123).",
        InputType = SettingInputType.Url)]
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted long-lived access token stored via ISensitiveDataProtector.
    /// Never surface this field raw in API responses — use the masked form instead.
    /// </summary>
    [JsonPropertyName("encryptedToken")]
    public string EncryptedToken { get; set; } = string.Empty;

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            throw new ValidationException("Home Assistant Base URL is required when enabled.");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ValidationException("Home Assistant Base URL must be a valid HTTP or HTTPS URL.");
        }
    }
}
