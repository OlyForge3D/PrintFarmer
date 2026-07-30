using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Farm.Web.Api.Tests.Controllers;

public class AdminTelegramControllerTests
{
    private readonly Mock<ISettingsService> _settingsService = new();
    private readonly Mock<ISensitiveDataProtector> _dataProtector = new();
    private readonly Mock<ITelegramNotificationSender> _telegramSender = new();

    [Fact]
    public void GetSettings_WhenTokenStored_ReturnsMaskedToken()
    {
        _settingsService.Setup(s => s.Get<TelegramSettings>())
            .Returns(new TelegramSettings
            {
                Enabled = true,
                ChatId = "987654",
                IncludeSnapshots = true,
                EncryptedBotToken = "enc:token"
            });
        _dataProtector.Setup(p => p.Unprotect("enc:token")).Returns("123456:abcdef");
        AdminTelegramController controller = CreateController();

        ActionResult<TelegramSettingsDto> result = controller.GetSettings();

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        TelegramSettingsDto dto = Assert.IsType<TelegramSettingsDto>(ok.Value);
        dto.BotTokenMasked.Should().Be("***cdef");
        dto.BotTokenMasked.Should().NotContain("123456:abcdef");
        dto.ChatId.Should().Be("987654");
        dto.IncludeSnapshots.Should().BeTrue();
    }

    [Fact]
    public void UpdateSettings_WhenValidRequest_EncryptsAndPersistsBotToken()
    {
        TelegramSettings stored = new();
        _settingsService.Setup(s => s.Get<TelegramSettings>()).Returns(stored);
        _dataProtector.Setup(p => p.Protect("123456:abcdef")).Returns("enc:new-token");
        AdminTelegramController controller = CreateController();

        ActionResult<TelegramSettingsDto> result = controller.UpdateSettings(new UpdateTelegramSettingsRequest
        {
            Enabled = true,
            ChatId = "987654",
            IncludeSnapshots = true,
            BotToken = "123456:abcdef"
        });

        Assert.IsType<OkObjectResult>(result.Result);
        _settingsService.Verify(s => s.Save(It.Is<TelegramSettings>(
            x => x.Enabled
                && x.ChatId == "987654"
                && x.IncludeSnapshots
                && x.EncryptedBotToken == "enc:new-token")),
            Times.Once);
    }

    [Fact]
    public void UpdateSettings_WhenTokenIsMaskedPlaceholder_LeavesExistingTokenUnchanged()
    {
        TelegramSettings stored = new() { EncryptedBotToken = "enc:existing" };
        _settingsService.Setup(s => s.Get<TelegramSettings>()).Returns(stored);
        AdminTelegramController controller = CreateController();

        ActionResult<TelegramSettingsDto> result = controller.UpdateSettings(new UpdateTelegramSettingsRequest
        {
            Enabled = false,
            ChatId = "987654",
            IncludeSnapshots = false,
            BotToken = "***cdef"
        });

        Assert.IsType<OkObjectResult>(result.Result);
        _dataProtector.Verify(p => p.Protect(It.IsAny<string>()), Times.Never);
        stored.EncryptedBotToken.Should().Be("enc:existing");
    }

    [Fact]
    public async Task SendTestMessageAsync_WhenConfigured_SendsTelegramMessage()
    {
        _settingsService.Setup(s => s.Get<TelegramSettings>())
            .Returns(new TelegramSettings
            {
                Enabled = true,
                ChatId = "987654",
                EncryptedBotToken = "enc:token"
            });
        _dataProtector.Setup(p => p.Unprotect("enc:token")).Returns("123456:abcdef");
        _telegramSender.Setup(s => s.SendMessageAsync(
                "123456:abcdef",
                "987654",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramDispatchResult(true));
        AdminTelegramController controller = CreateController();

        ActionResult<TelegramTestResult> result = await controller.SendTestMessageAsync(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        TelegramTestResult dto = Assert.IsType<TelegramTestResult>(ok.Value);
        dto.Success.Should().BeTrue();
        _telegramSender.Verify(s => s.SendMessageAsync(
                "123456:abcdef",
                "987654",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private AdminTelegramController CreateController()
    {
        return new AdminTelegramController(
            _settingsService.Object,
            _dataProtector.Object,
            _telegramSender.Object);
    }
}
