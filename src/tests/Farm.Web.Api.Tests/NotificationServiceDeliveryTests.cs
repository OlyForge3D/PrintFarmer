using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Notifications;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Email;
using Farm.Infrastructure.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests;

public class NotificationServiceDeliveryTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly Mock<INotificationRepository> _notificationRepository = new();
    private readonly Mock<IUsersRepository> _usersRepository = new();
    private readonly Mock<IEmailService> _emailService = new();
    private readonly Mock<IWebPushNotificationSender> _webPushSender = new();

    public NotificationServiceDeliveryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
    }

    [Fact]
    public async Task SendJobCompletedAsync_EmailEnabledForCompletedEvent_SendsEmail()
    {
        UserDto user = CreateUser();
        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { user });
        _emailService.Setup(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailDispatchResult(true));

        _dbContext.NotificationPreferences.Add(new NotificationPreferences
        {
            UserId = user.Id,
            EnableEmailNotifications = true,
            EnablePushNotifications = false,
            EnableInAppNotifications = false,
            EmailOnJobCompleted = true,
            EmailOnJobStarted = false,
            EmailOnJobFailed = false,
            EmailOnJobPaused = false,
            InAppOnJobCompleted = false,
            InAppOnJobFailed = true,
            InAppOnJobStarted = false,
            InAppOnJobPaused = false,
            PushOnJobCompleted = false,
            PushOnJobFailed = false,
            PushOnJobStarted = false,
            PushOnJobPaused = false
        });
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService();
        await service.SendJobCompletedAsync(Guid.NewGuid().ToString(), "Test", "Printer A");

        _emailService.Verify(
            x => x.SendAsync(
                It.Is<EmailMessage>(m => m.To == user.Email && m.Subject.Contains("completed", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendJobCompletedAsync_PushEnabledForCompletedEvent_SendsWebPush()
    {
        UserDto user = CreateUser();
        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { user });
        _webPushSender.Setup(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebPushDispatchResult(true));

        _dbContext.NotificationPreferences.Add(new NotificationPreferences
        {
            UserId = user.Id,
            EnableEmailNotifications = false,
            EnablePushNotifications = true,
            EnableInAppNotifications = false,
            EmailOnJobCompleted = false,
            EmailOnJobStarted = false,
            EmailOnJobFailed = false,
            EmailOnJobPaused = false,
            InAppOnJobCompleted = false,
            InAppOnJobFailed = true,
            InAppOnJobStarted = false,
            InAppOnJobPaused = false,
            PushOnJobCompleted = true,
            PushOnJobFailed = false,
            PushOnJobStarted = false,
            PushOnJobPaused = false
        });
        _dbContext.PushSubscriptions.Add(new PushSubscription
        {
            UserId = user.Id,
            Endpoint = "https://8.8.8.8/sub1",
            P256dh = "k1",
            Auth = "a1"
        });
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService();
        await service.SendJobCompletedAsync(Guid.NewGuid().ToString(), "Test", "Printer A");

        _webPushSender.Verify(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendJobCompletedAsync_CompletedEventDisabled_DoesNotSendAnyDelivery()
    {
        UserDto user = CreateUser();
        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { user });
        _emailService.Setup(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailDispatchResult(true));
        _webPushSender.Setup(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebPushDispatchResult(true));

        _dbContext.NotificationPreferences.Add(new NotificationPreferences
        {
            UserId = user.Id,
            EnableEmailNotifications = true,
            EnablePushNotifications = true,
            EnableInAppNotifications = true,
            EmailOnJobCompleted = false,
            PushOnJobCompleted = false,
            InAppOnJobCompleted = false
        });
        _dbContext.PushSubscriptions.Add(new PushSubscription
        {
            UserId = user.Id,
            Endpoint = "https://8.8.8.8/sub1",
            P256dh = "k1",
            Auth = "a1"
        });
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService();
        await service.SendJobCompletedAsync(Guid.NewGuid().ToString(), "Test", "Printer A");

        _notificationRepository.Verify(x => x.AddAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()), Times.Never);
        _emailService.Verify(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _webPushSender.Verify(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendJobFailedAsync_InAppDisabledInPreferences_StillCreatesInAppNotification()
    {
        UserDto user = CreateUser();
        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { user });

        _dbContext.NotificationPreferences.Add(new NotificationPreferences
        {
            UserId = user.Id,
            EnableEmailNotifications = false,
            EnablePushNotifications = false,
            EnableInAppNotifications = false,
            InAppOnJobFailed = false,
            EmailOnJobFailed = false,
            PushOnJobFailed = false
        });
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService();
        await service.SendJobFailedAsync(Guid.NewGuid().ToString(), "Test", "Jam detected");

        _notificationRepository.Verify(x => x.AddAsync(It.Is<Notification>(n => n.Type == NotificationType.JobFailed), It.IsAny<CancellationToken>()), Times.Once);
    }

    private UserDto CreateUser()
    {
        return new UserDto
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            Username = "user1",
            IsActive = true
        };
    }

    private NotificationService CreateService()
    {
        return new NotificationService(
            _notificationRepository.Object,
            _usersRepository.Object,
            NullLogger<NotificationService>.Instance,
            _dbContext,
            null,
            null,
            _emailService.Object,
            _webPushSender.Object);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
