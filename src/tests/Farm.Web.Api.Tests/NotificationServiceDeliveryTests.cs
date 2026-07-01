using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Notifications;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Email;
using Farm.Infrastructure.Services.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;
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
        _notificationRepository.Setup(x => x.AddAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public static IEnumerable<object[]> MatrixCellCases()
    {
        NotificationType[] eventTypes =
        [
            NotificationType.JobStarted,
            NotificationType.JobCompleted,
            NotificationType.JobFailed,
            NotificationType.JobPaused
        ];

        NotificationDeliveryChannel[] channels =
        [
            NotificationDeliveryChannel.InApp,
            NotificationDeliveryChannel.Email,
            NotificationDeliveryChannel.Push
        ];

        foreach (NotificationType eventType in eventTypes)
        {
            foreach (NotificationDeliveryChannel channel in channels)
            {
                yield return new object[] { eventType, channel, true };
                yield return new object[] { eventType, channel, false };
            }
        }
    }

    [Theory]
    [MemberData(nameof(MatrixCellCases))]
    public async Task SendJobNotificationAsync_EventChannelMatrix_EvaluatesConfiguredCell(NotificationType eventType, NotificationDeliveryChannel channel, bool enabled)
    {
        UserDto user = CreateUser();
        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { user });
        _emailService.Setup(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailDispatchResult(true));
        _webPushSender.Setup(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebPushDispatchResult(true));

        _dbContext.NotificationPreferences.Add(CreatePreferences(user.Id, eventType, channel, enabled));
        _dbContext.PushSubscriptions.Add(CreatePushSubscription(user.Id));
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService(endpointValidator: (_, _) => Task.FromResult(true));
        await SendJobNotificationAsync(service, eventType);

        VerifyChannelDelivery(eventType, channel, enabled);
    }

    [Fact]
    public async Task SendJobResumedAsync_JobPausedMatrixRowEnabled_DeliversConfiguredChannels()
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
            InAppOnJobPaused = true,
            EmailOnJobPaused = true,
            PushOnJobPaused = true
        });
        _dbContext.PushSubscriptions.Add(CreatePushSubscription(user.Id));
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService(endpointValidator: (_, _) => Task.FromResult(true));
        await service.SendJobResumedAsync(Guid.NewGuid().ToString(), "Test");

        _notificationRepository.Verify(x => x.AddAsync(It.Is<Notification>(n => n.Type == NotificationType.JobResumed), It.IsAny<CancellationToken>()), Times.Once);
        _emailService.Verify(x => x.SendAsync(It.Is<EmailMessage>(m => m.To == user.Email), It.IsAny<CancellationToken>()), Times.Once);
        _webPushSender.Verify(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendJobResumedAsync_JobPausedMatrixRowDisabled_SuppressesConfiguredChannels()
    {
        UserDto user = CreateUser();
        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { user });

        _dbContext.NotificationPreferences.Add(new NotificationPreferences
        {
            UserId = user.Id,
            EnableEmailNotifications = true,
            EnablePushNotifications = true,
            EnableInAppNotifications = true,
            InAppOnJobPaused = false,
            EmailOnJobPaused = false,
            PushOnJobPaused = false
        });
        _dbContext.PushSubscriptions.Add(CreatePushSubscription(user.Id));
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService(endpointValidator: (_, _) => Task.FromResult(true));
        await service.SendJobResumedAsync(Guid.NewGuid().ToString(), "Test");

        _notificationRepository.Verify(x => x.AddAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()), Times.Never);
        _emailService.Verify(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _webPushSender.Verify(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendJobCompletedAsync_EmailEnabledButUserEmailEmpty_DoesNotSendEmail()
    {
        UserDto user = CreateUser(email: string.Empty);
        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { user });

        _dbContext.NotificationPreferences.Add(CreatePreferences(user.Id, NotificationType.JobCompleted, NotificationDeliveryChannel.Email, enabled: true));
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService();
        await service.SendJobCompletedAsync(Guid.NewGuid().ToString(), "Test", "Printer A");

        _emailService.Verify(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendJobCompletedAsync_EmailServiceThrows_DoesNotPropagateException()
    {
        UserDto firstUser = CreateUser("first@example.com");
        UserDto secondUser = CreateUser("second@example.com");
        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { firstUser, secondUser });
        bool secondEmailDelivered = false;
        _emailService.Setup(x => x.SendAsync(
                It.Is<EmailMessage>(m => m.To == firstUser.Email),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP unavailable"));
        _emailService.Setup(x => x.SendAsync(
                It.Is<EmailMessage>(m => m.To == secondUser.Email),
                It.IsAny<CancellationToken>()))
            .Returns(async (EmailMessage message, CancellationToken ct) =>
            {
                await Task.Delay(100, ct);
                secondEmailDelivered = true;
                return new EmailDispatchResult(true);
            });

        _dbContext.NotificationPreferences.AddRange(
            CreatePreferences(firstUser.Id, NotificationType.JobCompleted, NotificationDeliveryChannel.Email, enabled: true),
            CreatePreferences(secondUser.Id, NotificationType.JobCompleted, NotificationDeliveryChannel.Email, enabled: true));
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService();
        Func<Task> act = () => service.SendJobCompletedAsync(Guid.NewGuid().ToString(), "Test", "Printer A");

        await act.Should().NotThrowAsync();
        _emailService.Verify(
            x => x.SendAsync(It.Is<EmailMessage>(m => m.To == firstUser.Email), It.IsAny<CancellationToken>()),
            Times.Once);
        _emailService.Verify(
            x => x.SendAsync(It.Is<EmailMessage>(m => m.To == secondUser.Email), It.IsAny<CancellationToken>()),
            Times.Once);
        secondEmailDelivered.Should().BeTrue();
    }

    [Fact]
    public async Task SendJobCompletedAsync_WebPushSenderThrows_DoesNotSkipRemainingTargets()
    {
        UserDto firstUser = CreateUser("first@example.com");
        UserDto secondUser = CreateUser("second@example.com");
        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { firstUser, secondUser });

        PushSubscription firstSubscription = CreatePushSubscription(firstUser.Id);
        PushSubscription secondSubscription = CreatePushSubscription(secondUser.Id);
        bool secondPushDelivered = false;
        _webPushSender.Setup(x => x.SendAsync(
                It.Is<PushSubscription>(s => s.UserId == firstUser.Id),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Push provider unavailable"));
        _webPushSender.Setup(x => x.SendAsync(
                It.Is<PushSubscription>(s => s.UserId == secondUser.Id),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (PushSubscription subscription, string payload, CancellationToken ct) =>
            {
                await Task.Delay(100, ct);
                secondPushDelivered = true;
                return new WebPushDispatchResult(true);
            });

        _dbContext.NotificationPreferences.AddRange(
            CreatePreferences(firstUser.Id, NotificationType.JobCompleted, NotificationDeliveryChannel.Push, enabled: true),
            CreatePreferences(secondUser.Id, NotificationType.JobCompleted, NotificationDeliveryChannel.Push, enabled: true));
        _dbContext.PushSubscriptions.AddRange(firstSubscription, secondSubscription);
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService(endpointValidator: (_, _) => Task.FromResult(true));
        Func<Task> act = () => service.SendJobCompletedAsync(Guid.NewGuid().ToString(), "Test", "Printer A");

        await act.Should().NotThrowAsync();
        _webPushSender.Verify(
            x => x.SendAsync(It.Is<PushSubscription>(s => s.UserId == firstUser.Id), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _webPushSender.Verify(
            x => x.SendAsync(It.Is<PushSubscription>(s => s.UserId == secondUser.Id), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        secondPushDelivered.Should().BeTrue();
    }

    [Fact]
    public async Task SendJobCompletedAsync_PushEnabledWithoutSubscriptions_DoesNotSendWebPush()
    {
        UserDto user = CreateUser();
        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { user });

        _dbContext.NotificationPreferences.Add(CreatePreferences(user.Id, NotificationType.JobCompleted, NotificationDeliveryChannel.Push, enabled: true));
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService(endpointValidator: (_, _) => Task.FromResult(true));
        await service.SendJobCompletedAsync(Guid.NewGuid().ToString(), "Test", "Printer A");

        _webPushSender.Verify(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendJobCompletedAsync_WebPushPayload_IncludesServiceWorkerTitleAndBodyFields()
    {
        UserDto user = CreateUser();
        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { user });
        string? capturedPayload = null;
        _webPushSender.Setup(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<PushSubscription, string, CancellationToken>((_, payload, _) => capturedPayload = payload)
            .ReturnsAsync(new WebPushDispatchResult(true));

        _dbContext.NotificationPreferences.Add(CreatePreferences(user.Id, NotificationType.JobCompleted, NotificationDeliveryChannel.Push, enabled: true));
        _dbContext.PushSubscriptions.Add(CreatePushSubscription(user.Id));
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService(endpointValidator: (_, _) => Task.FromResult(true));
        await service.SendJobCompletedAsync(Guid.NewGuid().ToString(), "Test", "Printer A");

        capturedPayload.Should().NotBeNull();
        using JsonDocument payload = JsonDocument.Parse(capturedPayload!);
        payload.RootElement.GetProperty("title").GetString().Should().Be("Job completed on Printer A");
        payload.RootElement.GetProperty("subject").GetString().Should().Be("Job completed on Printer A");
        payload.RootElement.GetProperty("body").GetString().Should().Be("Print job \"Test\" has completed successfully on Printer A.");
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
            Endpoint = "https://fcm.googleapis.com/sub1",
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
            Endpoint = "https://fcm.googleapis.com/sub1",
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

    [Fact]
    public async Task SendJobCompletedAsync_EndpointValidationFailsWithoutProviderExpiry_DoesNotRemoveSubscription()
    {
        UserDto user = CreateUser();
        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { user });

        _dbContext.NotificationPreferences.Add(new NotificationPreferences
        {
            UserId = user.Id,
            EnablePushNotifications = true,
            PushOnJobCompleted = true
        });

        var subscription = new PushSubscription
        {
            UserId = user.Id,
            Endpoint = "https://fcm.googleapis.com/sub-transient",
            P256dh = "k1",
            Auth = "a1"
        };
        _dbContext.PushSubscriptions.Add(subscription);
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService(endpointValidator: (_, _) => Task.FromResult(false));

        await service.SendJobCompletedAsync(Guid.NewGuid().ToString(), "Test", "Printer A");

        _webPushSender.Verify(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        int remaining = await _dbContext.PushSubscriptions.CountAsync(s => s.UserId == user.Id);
        remaining.Should().Be(1);
    }

    [Fact]
    public async Task SendJobCompletedAsync_ProviderSignalsSubscriptionExpired_RemovesSubscription()
    {
        UserDto user = CreateUser();
        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { user });
        _webPushSender.Setup(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebPushDispatchResult(Success: false, SubscriptionExpired: true, Error: "SubscriptionExpired"));

        _dbContext.NotificationPreferences.Add(new NotificationPreferences
        {
            UserId = user.Id,
            EnablePushNotifications = true,
            PushOnJobCompleted = true
        });

        _dbContext.PushSubscriptions.Add(new PushSubscription
        {
            UserId = user.Id,
            Endpoint = "https://fcm.googleapis.com/sub-expired",
            P256dh = "k1",
            Auth = "a1"
        });
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService(endpointValidator: (_, _) => Task.FromResult(true));

        await service.SendJobCompletedAsync(Guid.NewGuid().ToString(), "Test", "Printer A");

        int remaining = await _dbContext.PushSubscriptions.CountAsync(s => s.UserId == user.Id);
        remaining.Should().Be(0);
    }

    private static UserDto CreateUser(string email = "user@example.com")
    {
        return new UserDto
        {
            Id = Guid.NewGuid(),
            Email = email,
            Username = "user1",
            IsActive = true
        };
    }

    private static PushSubscription CreatePushSubscription(Guid userId)
    {
        return new PushSubscription
        {
            UserId = userId,
            Endpoint = $"https://fcm.googleapis.com/sub-{Guid.NewGuid():N}",
            P256dh = "k1",
            Auth = "a1"
        };
    }

    private static NotificationPreferences CreatePreferences(
        Guid userId,
        NotificationType eventType,
        NotificationDeliveryChannel channel,
        bool enabled)
    {
        var preferences = new NotificationPreferences
        {
            UserId = userId,
            EnableEmailNotifications = true,
            EnablePushNotifications = true,
            EnableInAppNotifications = true,
            InAppOnJobStarted = false,
            InAppOnJobCompleted = false,
            InAppOnJobFailed = false,
            InAppOnJobPaused = false,
            EmailOnJobStarted = false,
            EmailOnJobCompleted = false,
            EmailOnJobFailed = false,
            EmailOnJobPaused = false,
            PushOnJobStarted = false,
            PushOnJobCompleted = false,
            PushOnJobFailed = false,
            PushOnJobPaused = false
        };

        SetMatrixCell(preferences, eventType, channel, enabled);
        return preferences;
    }

    private static void SetMatrixCell(
        NotificationPreferences preferences,
        NotificationType eventType,
        NotificationDeliveryChannel channel,
        bool enabled)
    {
        switch (eventType, channel)
        {
            case (NotificationType.JobStarted, NotificationDeliveryChannel.InApp):
                preferences.InAppOnJobStarted = enabled;
                break;
            case (NotificationType.JobCompleted, NotificationDeliveryChannel.InApp):
                preferences.InAppOnJobCompleted = enabled;
                break;
            case (NotificationType.JobFailed, NotificationDeliveryChannel.InApp):
                preferences.InAppOnJobFailed = enabled;
                break;
            case (NotificationType.JobPaused, NotificationDeliveryChannel.InApp):
                preferences.InAppOnJobPaused = enabled;
                break;
            case (NotificationType.JobStarted, NotificationDeliveryChannel.Email):
                preferences.EmailOnJobStarted = enabled;
                break;
            case (NotificationType.JobCompleted, NotificationDeliveryChannel.Email):
                preferences.EmailOnJobCompleted = enabled;
                break;
            case (NotificationType.JobFailed, NotificationDeliveryChannel.Email):
                preferences.EmailOnJobFailed = enabled;
                break;
            case (NotificationType.JobPaused, NotificationDeliveryChannel.Email):
                preferences.EmailOnJobPaused = enabled;
                break;
            case (NotificationType.JobStarted, NotificationDeliveryChannel.Push):
                preferences.PushOnJobStarted = enabled;
                break;
            case (NotificationType.JobCompleted, NotificationDeliveryChannel.Push):
                preferences.PushOnJobCompleted = enabled;
                break;
            case (NotificationType.JobFailed, NotificationDeliveryChannel.Push):
                preferences.PushOnJobFailed = enabled;
                break;
            case (NotificationType.JobPaused, NotificationDeliveryChannel.Push):
                preferences.PushOnJobPaused = enabled;
                break;
        }
    }

    private static Task SendJobNotificationAsync(NotificationService service, NotificationType eventType)
    {
        string jobId = Guid.NewGuid().ToString();
        return eventType switch
        {
            NotificationType.JobStarted => service.SendJobStartedAsync(jobId, "Test", "Printer A"),
            NotificationType.JobCompleted => service.SendJobCompletedAsync(jobId, "Test", "Printer A"),
            NotificationType.JobFailed => service.SendJobFailedAsync(jobId, "Test", "Jam detected"),
            NotificationType.JobPaused => service.SendJobPausedAsync(jobId, "Test", "Filament change"),
            _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Unsupported notification type")
        };
    }

    private void VerifyChannelDelivery(NotificationType eventType, NotificationDeliveryChannel channel, bool enabled)
    {
        bool expectedInApp = channel == NotificationDeliveryChannel.InApp
            ? enabled || eventType == NotificationType.JobFailed
            : eventType == NotificationType.JobFailed;
        bool expectedEmail = channel == NotificationDeliveryChannel.Email && enabled;
        bool expectedPush = channel == NotificationDeliveryChannel.Push && enabled;

        _notificationRepository.Verify(
            x => x.AddAsync(It.Is<Notification>(n => n.Type == eventType), It.IsAny<CancellationToken>()),
            expectedInApp ? Times.Once() : Times.Never());
        _emailService.Verify(
            x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()),
            expectedEmail ? Times.Once() : Times.Never());
        _webPushSender.Verify(
            x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            expectedPush ? Times.Once() : Times.Never());
    }

    private NotificationService CreateService(Func<string, CancellationToken, Task<bool>>? endpointValidator = null)
    {
        return new NotificationService(
            _notificationRepository.Object,
            _usersRepository.Object,
            NullLogger<NotificationService>.Instance,
            _dbContext,
            null,
            null,
            _emailService.Object,
            _webPushSender.Object,
            endpointValidator);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
