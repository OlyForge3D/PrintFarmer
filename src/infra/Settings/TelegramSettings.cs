using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Farm-wide settings for Telegram notification delivery.
/// The bot token is encrypted and is never returned in plain text by API endpoints.
/// </summary>
/// <remarks>
/// This section is hidden from the generic settings surface — see the blocklist in
/// <c>UnifiedSettingsController</c> — because <see cref="EncryptedBotToken"/> is an ordinary
/// serialized property and would otherwise be served by <c>GET /api/settings/Telegram</c> and
/// overwritable by a plain save. It is edited through <c>AdminTelegramController</c> instead.
/// <para>
/// Consequence: the <c>Required</c> / <c>RequiredWhen</c> hints below are never read by the UI,
/// because the settings page only ever sees sections present in <c>GET /api/settings/metadata</c>.
/// They are kept as documentation of intent; <see cref="Validate"/> is what actually enforces
/// them, and the admin controller calls it before every save.
/// </para>
/// </remarks>
[AppSetting(SectionName)]
[SettingGroup("Integrations", DisplayName = "Integrations", Description = "External service integrations", Icon = "pf-icon-integration", Order = 5)]
[SettingDisplay(Name = "Telegram", Description = "Farm-wide Telegram notification channel settings.", Icon = "pf-icon-telegram", Group = "Integrations", Order = 3)]
public class TelegramSettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "Telegram";

    public static string SectionKey => SectionName;

    [SettingDisplay(
        Name = "Enabled",
        Description = "Enable Telegram notification delivery.",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [SettingDisplay(
        Name = "Chat ID",
        Description = "Telegram chat ID that receives PrintFarmer notifications.",
        InputType = SettingInputType.Text,
        Required = true,
        RequiredWhen = "enabled")]
    [JsonPropertyName("chatId")]
    public string ChatId { get; set; } = string.Empty;

    [SettingDisplay(
        Name = "Attach camera snapshots",
        Description = "Attach a printer camera snapshot to Telegram messages when available.",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("includeSnapshots")]
    public bool IncludeSnapshots { get; set; } = false;

    /// <summary>
    /// Encrypted Telegram bot token stored via ISensitiveDataProtector.
    /// Never surface this field raw in API responses.
    /// </summary>
    [JsonPropertyName("encryptedBotToken")]
    public string EncryptedBotToken { get; set; } = string.Empty;

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ChatId))
        {
            throw new ValidationException("Telegram Chat ID is required when Telegram notifications are enabled.");
        }

        if (string.IsNullOrWhiteSpace(EncryptedBotToken))
        {
            throw new ValidationException("Telegram bot token is required when Telegram notifications are enabled.");
        }
    }
}
