using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Farm-wide settings for Telegram notification delivery.
/// The bot token is encrypted and is never returned in plain text by API endpoints.
/// </summary>
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
        InputType = SettingInputType.Text)]
    [JsonPropertyName("chatId")]
    public string ChatId { get; set; } = string.Empty;

    [SettingDisplay(
        Name = "Attach Camera Snapshots",
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
