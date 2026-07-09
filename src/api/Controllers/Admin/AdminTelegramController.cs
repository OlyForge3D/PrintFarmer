using System.Text.Json.Serialization;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Admin;

/// <summary>
/// Admin endpoints for configuring and testing Telegram notifications.
/// </summary>
[ApiController]
[Route("api/admin/integrations/telegram")]
[Authorize(Roles = "farm_admin")]
[Tags("Admin - Telegram Notifications")]
public class AdminTelegramController(
    ISettingsService settingsService,
    ISensitiveDataProtector dataProtector,
    ITelegramNotificationSender telegramSender) : ControllerBase
{
    private const string TokenMaskPrefix = "***";

    /// <summary>
    /// Returns current Telegram settings. The bot token is always masked.
    /// </summary>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(TelegramSettingsDto), StatusCodes.Status200OK)]
    public ActionResult<TelegramSettingsDto> GetSettings()
    {
        TelegramSettings settings = settingsService.Get<TelegramSettings>();
        return Ok(MapToDto(settings));
    }

    /// <summary>
    /// Persists Telegram settings. Masked token values keep the existing stored token.
    /// </summary>
    [HttpPut("settings")]
    [ProducesResponseType(typeof(TelegramSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<TelegramSettingsDto> UpdateSettings([FromBody] UpdateTelegramSettingsRequest request)
    {
        TelegramSettings settings = settingsService.Get<TelegramSettings>();

        settings.Enabled = request.Enabled;
        settings.ChatId = request.ChatId?.Trim() ?? string.Empty;
        settings.IncludeSnapshots = request.IncludeSnapshots;

        if (!string.IsNullOrWhiteSpace(request.BotToken) &&
            !request.BotToken.StartsWith(TokenMaskPrefix, StringComparison.Ordinal))
        {
            settings.EncryptedBotToken = dataProtector.Protect(request.BotToken) ?? string.Empty;
        }

        try
        {
            settings.Validate();
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        settingsService.Save(settings);
        return Ok(MapToDto(settings));
    }

    /// <summary>
    /// Sends a Telegram test message using the current configuration.
    /// </summary>
    [HttpPost("test")]
    [ProducesResponseType(typeof(TelegramTestResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<TelegramTestResult>> SendTestMessageAsync(CancellationToken cancellationToken)
    {
        TelegramSettings settings = settingsService.Get<TelegramSettings>();
        if (!settings.Enabled)
        {
            return Ok(new TelegramTestResult(false, "Telegram notifications are disabled."));
        }

        string? botToken = string.IsNullOrWhiteSpace(settings.EncryptedBotToken)
            ? null
            : dataProtector.Unprotect(settings.EncryptedBotToken);
        string chatId = settings.ChatId.Trim();

        if (string.IsNullOrWhiteSpace(botToken))
        {
            return Ok(new TelegramTestResult(false, "Telegram bot token is not configured."));
        }

        if (string.IsNullOrWhiteSpace(chatId))
        {
            return Ok(new TelegramTestResult(false, "Telegram chat ID is not configured."));
        }

        TelegramDispatchResult result = await telegramSender.SendMessageAsync(
            botToken,
            chatId,
            "PrintFarmer Telegram notifications are configured correctly.",
            cancellationToken);

        return Ok(new TelegramTestResult(
            result.Success,
            result.Success ? "Test message sent." : result.Error ?? "Telegram test message failed."));
    }

    private TelegramSettingsDto MapToDto(TelegramSettings settings) => new()
    {
        Enabled = settings.Enabled,
        ChatId = settings.ChatId,
        IncludeSnapshots = settings.IncludeSnapshots,
        BotTokenMasked = MaskToken(settings.EncryptedBotToken, dataProtector)
    };

    private static string MaskToken(string encryptedToken, ISensitiveDataProtector protector)
    {
        if (string.IsNullOrWhiteSpace(encryptedToken))
        {
            return string.Empty;
        }

        string? plain = protector.Unprotect(encryptedToken);
        if (string.IsNullOrWhiteSpace(plain) || plain.Length <= 4)
        {
            return TokenMaskPrefix;
        }

        return $"{TokenMaskPrefix}{plain[^4..]}";
    }
}

/// <summary>Returned by GET /settings. The bot token is always masked.</summary>
public sealed class TelegramSettingsDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("chatId")]
    public string ChatId { get; set; } = string.Empty;

    [JsonPropertyName("includeSnapshots")]
    public bool IncludeSnapshots { get; set; }

    [JsonPropertyName("botTokenMasked")]
    public string BotTokenMasked { get; set; } = string.Empty;
}

/// <summary>Used by PUT /settings.</summary>
public sealed class UpdateTelegramSettingsRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("chatId")]
    public string? ChatId { get; set; }

    [JsonPropertyName("includeSnapshots")]
    public bool IncludeSnapshots { get; set; }

    [JsonPropertyName("botToken")]
    public string? BotToken { get; set; }
}

/// <summary>Returned by POST /test.</summary>
public sealed record TelegramTestResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message);
