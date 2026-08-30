using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Notifications;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Email;
using Farm.Infrastructure.Services.Notifications;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    private readonly Mock<INotificationChannel> _telegramChannel = new();

    public NotificationServiceDeliveryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        _notificationRepository.Setup(x => x.AddAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _notificationRepository.Setup(x => x.AddRangeAsync(It.IsAny<IEnumerable<Notification>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _telegramChannel.SetupGet(x => x.Channel).Returns(NotificationDeliveryChannel.Telegram);
        _telegramChannel.Setup(x => x.SendAsync(It.IsAny<NotificationChannelMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NotificationChannelDispatchResult.Succeeded);
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
            NotificationDeliveryChannel.Push,
            NotificationDeliveryChannel.Telegram
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

        _notificationRepository.Verify(
            x => x.AddRangeAsync(
                It.Is<IEnumerable<Notification>>(notifications => notifications.Count(n => n.Type == NotificationType.JobResumed) == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);
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

        _notificationRepository.Verify(x => x.AddRangeAsync(It.IsAny<IEnumerable<Notification>>(), It.IsAny<CancellationToken>()), Times.Never);
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

        _notificationRepository.Verify(x => x.AddRangeAsync(It.IsAny<IEnumerable<Notification>>(), It.IsAny<CancellationToken>()), Times.Never);
        _emailService.Verify(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        _webPushSender.Verify(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendJobCompletedAsync_MultipleUsersWithMixedPreferences_BatchesQueriesAndPersistsExactRecipients()
    {
        // Issue #2226 regression coverage: exercises the batched preferences/push-subscription
        // reads and the single AddRangeAsync write across FOUR distinct recipient shapes in one
        // broadcast, which a single- or dual-user test cannot distinguish from a correct
        // per-user implementation:
        //   - userWithPrefs: an explicit, enabled NotificationPreferences row + 2 push subs.
        //   - userWithoutPrefs: NO preferences row at all -> must fall back to the canonical
        //     defaults (BuildDefaultPreferences) via the batched dictionary, not be silently
        //     dropped because it has no dictionary entry. Has a stored push subscription so
        //     BOTH the in-app and push fallback are proven, not just in-app.
        //   - userWithDisabledPush: an explicit row with InApp/Push disabled but a STORED push
        //     subscription -> the subscription must never be queried/dispatched.
        //   - inactiveUser: IsActive = false -> must be excluded from every query and every
        //     dispatch channel entirely.
        UserDto userWithPrefs = CreateUser("with-prefs@example.com");
        UserDto userWithoutPrefs = CreateUser("without-prefs@example.com");
        UserDto userWithDisabledPush = CreateUser("disabled-push@example.com");
        UserDto inactiveUser = CreateUser("inactive@example.com");
        inactiveUser.IsActive = false;

        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { userWithPrefs, userWithoutPrefs, userWithDisabledPush, inactiveUser });
        _webPushSender.Setup(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebPushDispatchResult(true));

        _dbContext.NotificationPreferences.Add(new NotificationPreferences
        {
            UserId = userWithPrefs.Id,
            EnableInAppNotifications = true,
            EnablePushNotifications = true,
            InAppOnJobCompleted = true,
            PushOnJobCompleted = true
        });
        _dbContext.NotificationPreferences.Add(new NotificationPreferences
        {
            UserId = userWithDisabledPush.Id,
            EnableInAppNotifications = true,
            EnablePushNotifications = true,
            InAppOnJobCompleted = false,
            PushOnJobCompleted = false
        });
        // inactiveUser gets a fully-enabled row too, proving IsActive (not preference
        // absence) is what excludes them.
        _dbContext.NotificationPreferences.Add(new NotificationPreferences
        {
            UserId = inactiveUser.Id,
            EnableInAppNotifications = true,
            EnablePushNotifications = true,
            InAppOnJobCompleted = true,
            PushOnJobCompleted = true
        });

        PushSubscription subA1 = CreatePushSubscription(userWithPrefs.Id);
        PushSubscription subA2 = CreatePushSubscription(userWithPrefs.Id);
        PushSubscription subDefault = CreatePushSubscription(userWithoutPrefs.Id);
        PushSubscription subDisabled = CreatePushSubscription(userWithDisabledPush.Id);
        PushSubscription subInactive = CreatePushSubscription(inactiveUser.Id);
        _dbContext.PushSubscriptions.AddRange(subA1, subA2, subDefault, subDisabled, subInactive);
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService(endpointValidator: (_, _) => Task.FromResult(true));
        await service.SendJobCompletedAsync(Guid.NewGuid().ToString(), "Test", "Printer A");

        // NotificationPreferencesDefaults.Create defaults InAppOnJobCompleted/PushOnJobCompleted
        // to true, so userWithoutPrefs (no row) must still receive both channels via fallback -
        // proven for BOTH channels: the in-app assertion above, and subDefault's dispatch below.
        _notificationRepository.Verify(
            x => x.AddRangeAsync(
                It.Is<IEnumerable<Notification>>(notifications =>
                    notifications.Select(n => n.UserId).OrderBy(id => id).SequenceEqual(
                        new[] { userWithPrefs.Id, userWithoutPrefs.Id }.OrderBy(id => id))
                    && notifications.All(n => n.Type == NotificationType.JobCompleted)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _webPushSender.Verify(
            x => x.SendAsync(It.Is<PushSubscription>(s => s.Id == subA1.Id), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _webPushSender.Verify(
            x => x.SendAsync(It.Is<PushSubscription>(s => s.Id == subA2.Id), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _webPushSender.Verify(
            x => x.SendAsync(It.Is<PushSubscription>(s => s.Id == subDefault.Id), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _webPushSender.Verify(
            x => x.SendAsync(It.Is<PushSubscription>(s => s.Id == subDisabled.Id), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _webPushSender.Verify(
            x => x.SendAsync(It.Is<PushSubscription>(s => s.Id == subInactive.Id), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _webPushSender.Verify(
            x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task SendJobCompletedAsync_MultipleActiveUsers_ResolvesPreferencesAndPushSubscriptionsWithConstantQueryCount()
    {
        // Issue #2226: proves the read side is genuinely batched (a fixed, small number of SQL
        // commands regardless of recipient count) using a real SQLite connection + command
        // counter, rather than only asserting on outcomes via mocks. Uses its own DbContext
        // (not the shared in-memory-provider _dbContext) because the InMemory provider does not
        // execute DbCommands, so a command-counting interceptor cannot observe it.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        var interceptor = new CommandCountingInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var sqliteDbContext = new AppDbContext(options);
        await sqliteDbContext.Database.EnsureCreatedAsync();

        var users = new List<UserDto>();
        for (int i = 0; i < 12; i++)
        {
            UserDto user = CreateUser($"user{i}@example.com");
            users.Add(user);
            sqliteDbContext.Users.Add(new User
            {
                Id = user.Id,
                Username = $"user{i}",
                Email = user.Email,
                PasswordHash = "x"
            });
            sqliteDbContext.NotificationPreferences.Add(new NotificationPreferences
            {
                UserId = user.Id,
                EnableInAppNotifications = true,
                EnablePushNotifications = true,
                InAppOnJobCompleted = true,
                PushOnJobCompleted = true
            });
            sqliteDbContext.PushSubscriptions.Add(CreatePushSubscription(user.Id));
        }

        await sqliteDbContext.SaveChangesAsync();
        interceptor.Reset();

        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(users);
        // Success:false avoids the unrelated "mark subscription LastUsedAt" write path
        // (an existing, pre-#2226 per-successful-target update) so this assertion isolates
        // the batched-read behaviour this issue is actually about, instead of conflating it
        // with SQLite's lack of UPDATE batching for that separate write.
        _webPushSender.Setup(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebPushDispatchResult(false));

        var service = new NotificationService(
            _notificationRepository.Object,
            _usersRepository.Object,
            NullLogger<NotificationService>.Instance,
            sqliteDbContext,
            null,
            null,
            _emailService.Object,
            _webPushSender.Object,
            (_, _) => Task.FromResult(true),
            [_telegramChannel.Object]);

        await service.SendJobCompletedAsync(Guid.NewGuid().ToString(), "Test", "Printer A");

        // Two batched queries for preferences + push subscriptions, plus one query to resolve
        // the job's printer id (ResolvePrinterIdForJobAsync, unrelated to this issue and
        // already a single query) = 3 commands total, independent of the 12 recipients above
        // (constant within a single chunk; see ChunkUserIds_ProducesOneChunkPerBatchOf500Users
        // below for the chunking behavior above UserIdBatchSize=500, which this fixed 12-user
        // run cannot exercise). A per-user N+1 implementation would issue 24+ commands instead.
        interceptor.CommandCount.Should().Be(3);
        _notificationRepository.Verify(
            x => x.AddRangeAsync(
                It.Is<IEnumerable<Notification>>(notifications => notifications.Count() == 12),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _webPushSender.Verify(
            x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(12));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(500, 1)]
    [InlineData(501, 2)]
    [InlineData(1000, 2)]
    [InlineData(1001, 3)]
    public void ChunkUserIds_ProducesCeilingDivisionByUserIdBatchSizeAcrossThe500Boundary(int userCount, int expectedChunkCount)
    {
        // Issue #2226 follow-up (Hicks round-2): the read side is batched per-chunk of
        // UserIdBatchSize=500, not O(1) - the query-count test above only exercises 12 users,
        // well below that boundary, so it cannot observe chunking behavior at the boundary.
        // This test drives ChunkUserIds directly (via reflection, since it's a private
        // implementation detail with no public surface) across 500/501/1000/1001 users to
        // prove every user id is preserved, none are duplicated, and no chunk exceeds the
        // batch size - rather than relying on a small fixed sample to accidentally hide a
        // chunking regression.
        List<Guid> userIds = Enumerable.Range(0, userCount).Select(_ => Guid.NewGuid()).ToList();

        MethodInfo chunkMethod = typeof(NotificationService).GetMethod(
            "ChunkUserIds", BindingFlags.NonPublic | BindingFlags.Static)!;
        var chunks = ((IEnumerable<IReadOnlyList<Guid>>)chunkMethod.Invoke(null, [userIds])!).ToList();

        chunks.Should().HaveCount(expectedChunkCount);
        chunks.SelectMany(chunk => chunk).Should().BeEquivalentTo(userIds);
        chunks.Should().OnlyContain(chunk => chunk.Count > 0 && chunk.Count <= 500);
    }

    [Fact]
    public async Task SendJobCompletedAsync_InAppPersistenceFails_StillDispatchesEmailPushAndTelegram()
    {
        // Issue #2226 follow-up (Bishop/Vasquez round-2): proves the try/catch isolating the
        // batched AddRangeAsync call in BroadcastJobNotificationAsync has the effect its
        // in-code comment claims - a persistence failure must not prevent, or even delay,
        // email/push/telegram dispatch for the same event. The OLD per-user loop called the
        // in-app save inline in the same loop that built the other channels' dispatch lists,
        // so any single save failure aborted the whole broadcast (verified via
        // `git show 3da69dd37`); this test is the regression guard for that fix.
        UserDto user = CreateUser();
        _usersRepository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserDto> { user });
        _notificationRepository
            .Setup(x => x.AddRangeAsync(It.IsAny<IEnumerable<Notification>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated persistence failure"));
        _webPushSender.Setup(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebPushDispatchResult(true));

        _dbContext.NotificationPreferences.Add(new NotificationPreferences
        {
            UserId = user.Id,
            EnableInAppNotifications = true,
            EnableEmailNotifications = true,
            EnablePushNotifications = true,
            EnableTelegramNotifications = true,
            InAppOnJobCompleted = true,
            EmailOnJobCompleted = true,
            PushOnJobCompleted = true,
            TelegramOnJobCompleted = true
        });
        _dbContext.PushSubscriptions.Add(CreatePushSubscription(user.Id));
        await _dbContext.SaveChangesAsync();

        NotificationService service = CreateService(endpointValidator: (_, _) => Task.FromResult(true));

        // Must not throw out of the broadcast: if the isolation regressed back to the old
        // uncaught-in-loop behavior, this would propagate and the assertions below would never
        // run because dispatch would have been skipped entirely.
        Func<Task> act = () => service.SendJobCompletedAsync(Guid.NewGuid().ToString(), "Test", "Printer A");
        await act.Should().NotThrowAsync();

        _emailService.Verify(x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        _webPushSender.Verify(x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _telegramChannel.Verify(x => x.SendAsync(It.IsAny<NotificationChannelMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddRangeAsync_SaveChangesFails_DetachesFailedBatchAndDoesNotPoisonSubsequentSave()
    {
        // Issue #2226 follow-up (Hicks round-2 finding #3): exercises EfNotificationRepository
        // directly against a real relational (SQLite) provider to prove the batch commit is
        // genuinely atomic and the change-tracker-detach fix actually prevents poisoning a
        // shared DbContext, rather than only asserting a single mocked repository call. A
        // Notification with a UserId that has no matching User row violates the FK configured
        // in NotificationConfiguration.cs, forcing a real DbUpdateException from
        // SaveChangesAsync instead of a fabricated one.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var sqliteDbContext = new AppDbContext(options);
        await sqliteDbContext.Database.EnsureCreatedAsync();

        var validUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "valid-user",
            Email = "valid-user@example.com",
            PasswordHash = "x"
        };
        sqliteDbContext.Users.Add(validUser);
        await sqliteDbContext.SaveChangesAsync();

        var repository = new EfNotificationRepository(sqliteDbContext);

        var failingBatch = new List<Notification>
        {
            new()
            {
                UserId = validUser.Id,
                Type = NotificationType.JobCompleted,
                Subject = "Valid",
                Body = "Valid notification",
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                // No matching User row -> violates the required FK and forces SaveChangesAsync
                // to throw for the whole batch.
                UserId = Guid.NewGuid(),
                Type = NotificationType.JobCompleted,
                Subject = "Invalid",
                Body = "Orphaned notification",
                CreatedAt = DateTime.UtcNow
            }
        };

        Func<Task> act = async () => await repository.AddRangeAsync(failingBatch, CancellationToken.None);
        await act.Should().ThrowAsync<DbUpdateException>();

        // The whole batch must be rolled back atomically (no partial commit)...
        (await sqliteDbContext.Notifications.CountAsync()).Should().Be(0);
        // ...and every entity from the failed batch must be detached from the change tracker,
        // not left dangling in the Added state where a later, unrelated SaveChangesAsync on
        // this same scoped context could unintentionally flush them.
        sqliteDbContext.ChangeTracker.Entries<Notification>().Should().BeEmpty();

        // A subsequent, valid save on the SAME DbContext instance must succeed cleanly -
        // proving the failed batch left no residual state behind.
        var subsequentBatch = new List<Notification>
        {
            new()
            {
                UserId = validUser.Id,
                Type = NotificationType.JobCompleted,
                Subject = "Recovered",
                Body = "Persisted after the earlier failure",
                CreatedAt = DateTime.UtcNow
            }
        };
        await repository.AddRangeAsync(subsequentBatch, CancellationToken.None);

        (await sqliteDbContext.Notifications.CountAsync()).Should().Be(1);
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

        _notificationRepository.Verify(
            x => x.AddRangeAsync(
                It.Is<IEnumerable<Notification>>(notifications => notifications.Count(n => n.Type == NotificationType.JobFailed) == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);
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

    [Fact]
    public async Task UpdatePreferencesAsync_ExistingPreferences_PersistsTelegramMatrixFields()
    {
        UserDto user = CreateUser();
        _dbContext.NotificationPreferences.Add(new NotificationPreferences
        {
            UserId = user.Id,
            EnableTelegramNotifications = false,
            TelegramOnJobCompleted = false,
            TelegramOnJobFailed = false
        });
        await _dbContext.SaveChangesAsync();
        NotificationService service = CreateService();

        await service.UpdatePreferencesAsync(user.Id, new NotificationPreferences
        {
            UserId = user.Id,
            EnableEmailNotifications = false,
            EnablePushNotifications = false,
            EnableInAppNotifications = true,
            EnableTelegramNotifications = true,
            InAppOnJobFailed = true,
            TelegramOnJobCompleted = true,
            TelegramOnJobFailed = true,
            Frequency = NotificationFrequency.RealTime,
            RetentionDays = 14
        });

        NotificationPreferences stored = await _dbContext.NotificationPreferences.SingleAsync(p => p.UserId == user.Id);
        stored.EnableTelegramNotifications.Should().BeTrue();
        stored.TelegramOnJobCompleted.Should().BeTrue();
        stored.TelegramOnJobFailed.Should().BeTrue();
        stored.RetentionDays.Should().Be(14);
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
            EnableTelegramNotifications = true,
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
            PushOnJobPaused = false,
            TelegramOnJobStarted = false,
            TelegramOnJobCompleted = false,
            TelegramOnJobFailed = false,
            TelegramOnJobPaused = false
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
            case (NotificationType.JobStarted, NotificationDeliveryChannel.Telegram):
                preferences.TelegramOnJobStarted = enabled;
                break;
            case (NotificationType.JobCompleted, NotificationDeliveryChannel.Telegram):
                preferences.TelegramOnJobCompleted = enabled;
                break;
            case (NotificationType.JobFailed, NotificationDeliveryChannel.Telegram):
                preferences.TelegramOnJobFailed = enabled;
                break;
            case (NotificationType.JobPaused, NotificationDeliveryChannel.Telegram):
                preferences.TelegramOnJobPaused = enabled;
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
        bool expectedTelegram = channel == NotificationDeliveryChannel.Telegram && enabled;

        _notificationRepository.Verify(
            x => x.AddRangeAsync(
                It.Is<IEnumerable<Notification>>(notifications => notifications.Count(n => n.Type == eventType) == 1),
                It.IsAny<CancellationToken>()),
            expectedInApp ? Times.Once() : Times.Never());
        _emailService.Verify(
            x => x.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()),
            expectedEmail ? Times.Once() : Times.Never());
        _webPushSender.Verify(
            x => x.SendAsync(It.IsAny<PushSubscription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            expectedPush ? Times.Once() : Times.Never());
        _telegramChannel.Verify(
            x => x.SendAsync(
                It.Is<NotificationChannelMessage>(m => m.Type == eventType),
                It.IsAny<CancellationToken>()),
            expectedTelegram ? Times.Once() : Times.Never());
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
            endpointValidator,
            [_telegramChannel.Object]);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        public int CommandCount { get; private set; }

        public void Reset()
        {
            CommandCount = 0;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return ValueTask.FromResult(result);
        }
    }
}
