using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests;

public class TelegramNotificationChannelTests
{
    private readonly Mock<ISettingsService> _settingsService = new();
    private readonly Mock<ISensitiveDataProtector> _dataProtector = new();
    private readonly Mock<ITelegramNotificationSender> _sender = new();
    private readonly Mock<IPrintersService> _printersService = new();

    [Fact]
    public async Task SendAsync_WhenSnapshotEnabledAndCameraAvailable_SendsPhoto()
    {
        Guid printerId = Guid.NewGuid();
        ConfigureEnabledSettings(includeSnapshots: true);
        _printersService.Setup(x => x.GetCameraSnapshotAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([1, 2, 3]);
        _sender.Setup(x => x.SendPhotoAsync("plain-token", "987654", It.IsAny<string>(), It.IsAny<byte[]>(), "image/jpeg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramDispatchResult(true));
        TelegramNotificationChannel channel = CreateChannel();

        NotificationChannelDispatchResult result = await channel.SendAsync(
            new NotificationChannelMessage(Farm.Infrastructure.Domain.Notifications.NotificationType.JobCompleted, "Done", "Body", PrinterId: printerId),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        _sender.Verify(x => x.SendPhotoAsync("plain-token", "987654", It.IsAny<string>(), It.Is<byte[]>(b => b.SequenceEqual(new byte[] { 1, 2, 3 })), "image/jpeg", It.IsAny<CancellationToken>()), Times.Once);
        _sender.Verify(x => x.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_WhenSnapshotMissing_FallsBackToMessage()
    {
        Guid printerId = Guid.NewGuid();
        ConfigureEnabledSettings(includeSnapshots: true);
        _printersService.Setup(x => x.GetCameraSnapshotAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);
        _sender.Setup(x => x.SendMessageAsync("plain-token", "987654", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramDispatchResult(true));
        TelegramNotificationChannel channel = CreateChannel();

        NotificationChannelDispatchResult result = await channel.SendAsync(
            new NotificationChannelMessage(Farm.Infrastructure.Domain.Notifications.NotificationType.JobCompleted, "Done", "Body", PrinterId: printerId),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        _sender.Verify(x => x.SendMessageAsync("plain-token", "987654", "Done\n\nBody", It.IsAny<CancellationToken>()), Times.Once);
        _sender.Verify(x => x.SendPhotoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void ConfigureEnabledSettings(bool includeSnapshots)
    {
        _settingsService.Setup(x => x.Get<TelegramSettings>())
            .Returns(new TelegramSettings
            {
                Enabled = true,
                ChatId = "987654",
                IncludeSnapshots = includeSnapshots,
                EncryptedBotToken = "enc-token"
            });
        _dataProtector.Setup(x => x.Unprotect("enc-token")).Returns("plain-token");
    }

    private TelegramNotificationChannel CreateChannel()
    {
        return new TelegramNotificationChannel(
            _settingsService.Object,
            _dataProtector.Object,
            _sender.Object,
            _printersService.Object,
            NullLogger<TelegramNotificationChannel>.Instance);
    }
}
