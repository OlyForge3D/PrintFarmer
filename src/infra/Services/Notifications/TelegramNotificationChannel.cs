using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Notifications;

/// <summary>
/// Farm-wide Telegram notification channel backed by the configured bot token and chat ID.
/// </summary>
public sealed class TelegramNotificationChannel(
    ISettingsService settingsService,
    ISensitiveDataProtector dataProtector,
    ITelegramNotificationSender sender,
    IPrintersService printersService,
    ILogger<TelegramNotificationChannel> logger) : INotificationChannel
{
    public NotificationDeliveryChannel Channel => NotificationDeliveryChannel.Telegram;

    public async Task<NotificationChannelDispatchResult> SendAsync(
        NotificationChannelMessage message,
        CancellationToken cancellationToken)
    {
        TelegramSettings settings = settingsService.Get<TelegramSettings>();
        if (!settings.Enabled)
        {
            return NotificationChannelDispatchResult.Succeeded;
        }

        string? botToken = string.IsNullOrWhiteSpace(settings.EncryptedBotToken)
            ? null
            : dataProtector.Unprotect(settings.EncryptedBotToken);
        string chatId = settings.ChatId.Trim();

        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
        {
            logger.LogDebug("Skipping Telegram notification because Telegram is not fully configured.");
            return NotificationChannelDispatchResult.Succeeded;
        }

        string text = FormatMessage(message);
        TelegramDispatchResult result = await TrySendWithSnapshotAsync(
            settings,
            botToken,
            chatId,
            text,
            message.PrinterId,
            cancellationToken);

        return result.Success
            ? NotificationChannelDispatchResult.Succeeded
            : new NotificationChannelDispatchResult(false, result.Error);
    }

    private async Task<TelegramDispatchResult> TrySendWithSnapshotAsync(
        TelegramSettings settings,
        string botToken,
        string chatId,
        string text,
        Guid? printerId,
        CancellationToken cancellationToken)
    {
        if (settings.IncludeSnapshots && printerId.HasValue)
        {
            try
            {
                byte[]? snapshot = await printersService.GetCameraSnapshotAsync(printerId.Value, cancellationToken);
                if (snapshot is { Length: > 0 })
                {
                    return await sender.SendPhotoAsync(botToken, chatId, text, snapshot, "image/jpeg", cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to attach Telegram snapshot for printer {PrinterId}; falling back to text.",
                    printerId);
            }
        }

        return await sender.SendMessageAsync(botToken, chatId, text, cancellationToken);
    }

    private static string FormatMessage(NotificationChannelMessage message)
    {
        return string.IsNullOrWhiteSpace(message.Body)
            ? message.Subject
            : $"{message.Subject}\n\n{message.Body}";
    }
}
