using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Persisted settings for the optional Home Assistant smart plug integration.
/// The long-lived access token is stored encrypted (via ISensitiveDataProtector)
/// in <see cref="EncryptedToken"/>; it is never returned in plain form to API clients.
/// </summary>
/// <remarks>
/// This section is hidden from the generic settings surface — see the blocklist in
/// <c>UnifiedSettingsController</c> — because <see cref="EncryptedToken"/> is an ordinary
/// serialized property and would otherwise be served by <c>GET /api/settings/HomeAssistant</c>
/// and overwritable by a plain save. It is edited through <c>AdminHomeAssistantController</c>.
/// <para>
/// Consequence: the <c>Required</c> / <c>RequiredWhen</c> hints below are never read by the UI,
/// because the settings page only ever sees sections present in <c>GET /api/settings/metadata</c>.
/// They are kept as documentation of intent; <see cref="Validate"/> is what actually enforces
/// them, and the admin controller calls it before every save.
/// </para>
/// </remarks>
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
        InputType = SettingInputType.Url,
        Required = true,
        RequiredWhen = "enabled")]
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
