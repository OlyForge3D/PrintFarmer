using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Settings;

namespace Farm.Web.Api.Services.OctoPrint;

/// <summary>
/// Settings for OctoPrint/Slicer compatibility API - controls authentication and rate limiting
/// for connections from slicers like OrcaSlicer, PrusaSlicer, etc.
/// </summary>
[AppSetting(OctoPrintSettings.SectionName)]
[SettingDisplay(Name = "OctoPrint/Slicer API", Description = "Authentication and rate limiting for slicer connections (OrcaSlicer, PrusaSlicer, etc.).", Icon = "pf-icon-slicer", Group = "Integrations", Order = 11)]
public class OctoPrintSettings : IAppSetting
{
    public const string SectionName = "OctoPrint";

    public static string SectionKey => SectionName;

    /// <summary>
    /// When enabled, slicer uploads require a valid API key in the X-Api-Key header.
    /// When disabled, uploads are allowed without authentication (useful for local/trusted networks).
    /// </summary>
    [JsonPropertyName("requireApiKey")]
    [DisplayName("Require API Key")]
    [Description("When enabled, slicer connections (OrcaSlicer, PrusaSlicer, etc.) must provide a valid API key. Disable for trusted local networks.")]
    public bool RequireApiKey { get; set; } = false;

    /// <summary>
    /// When enabled, API keys are stored as SHA256 hashes for security.
    /// When disabled, keys are stored in plain text (useful for debugging but less secure).
    /// </summary>
    [JsonPropertyName("hashStoredApiKeys")]
    [DisplayName("Hash Stored API Keys")]
    [Description("Store API keys as SHA256 hashes for improved security. Disable only for debugging purposes.")]
    public bool HashStoredApiKeys { get; set; } = true;

    /// <summary>
    /// Maximum number of upload requests per minute from a single IP or API key.
    /// </summary>
    [JsonPropertyName("rateLimitPerMinute")]
    [DisplayName("Rate Limit (per minute)")]
    [Description("Maximum upload requests per minute from a single source. Set to 0 to disable rate limiting.")]
    [Range(0, 1000)]
    public int RateLimitPerMinute { get; set; } = 60;

    /// <summary>
    /// Maximum file size allowed for uploads in megabytes.
    /// </summary>
    [JsonPropertyName("maxUploadSizeMb")]
    [DisplayName("Max Upload Size (MB)")]
    [Description("Maximum file size for slicer uploads in megabytes.")]
    [Range(1, 500)]
    public int MaxUploadSizeMb { get; set; } = 50;
}
