using System.Collections.Concurrent;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.Notifications.NativePush;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Notifications;

public sealed class NotificationServicePreferenceRaceTests
{
    [Fact]
    public async Task FirstCreate_BothReadMissing_WinnerCommitsBeforeFreshRetryReread()
    {
        await using var host = new CustomWebApplicationFactory();
        using var client = host.CreateClient();
        IDbContextFactory<AppDbContext> factory =
            host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        factory.GetType().Name.Should().Contain("PooledDbContextFactory");
        Guid userId = Guid.NewGuid();
        await SeedUserAsync(factory, userId, createPreferences: false);
        var coordinator = new AdverseRaceCoordinator(
            userId,
            initialRowShouldExist: false,
            retryWinnerPredicate: preferences => preferences?.TelegramOnMaintenanceDue == true);
        NotificationPreferencesUpdate loserPatch = MatrixPatch(
            NotificationPreferenceEvent.JobStarted,
            inApp: false,
            email: true,
            push: false,
            telegram: false);
        NotificationPreferencesUpdate winnerPatch = MatrixPatch(
            NotificationPreferenceEvent.MaintenanceDue,
            inApp: false,
            email: false,
            push: false,
            telegram: true);

        Task loser = Task.Run(() => RunPreferencePatchAsync(
            host,
            userId,
            loserPatch,
            coordinator.LoserReadHookAsync,
            coordinator.ClassifyAfterWinnerCommit));
        Task winner = Task.Run(() => RunWinnerAsync(
            () => RunPreferencePatchAsync(
                host,
                userId,
                winnerPatch,
                coordinator.WinnerReadHookAsync,
                classifier: null),
            coordinator));
        await Task.WhenAll(loser, winner).WaitAsync(TimeSpan.FromSeconds(30));

        await using AppDbContext verify = await factory.CreateDbContextAsync();
        NotificationPreferences[] rows = await verify.NotificationPreferences
            .AsNoTracking()
            .Where(preferences => preferences.UserId == userId)
            .ToArrayAsync();
        rows.Should().ContainSingle();
        rows[0].EmailOnJobStarted.Should().BeTrue();
        rows[0].TelegramOnMaintenanceDue.Should().BeTrue();
        coordinator.AssertExactOrderingAndRetry();
    }

    [Fact]
    public async Task DisjointCategoryUpdates_BothReadSameMap_WinnerCommitsBeforeFreshRetryMerge()
    {
        await using var host = new CustomWebApplicationFactory();
        using var client = host.CreateClient();
        IDbContextFactory<AppDbContext> factory =
            host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        factory.GetType().Name.Should().Contain("PooledDbContextFactory");
        Guid userId = Guid.NewGuid();
        await SeedUserAsync(factory, userId, createPreferences: true);
        var coordinator = new AdverseRaceCoordinator(
            userId,
            initialRowShouldExist: true,
            retryWinnerPredicate: preferences =>
                preferences is not null
                && AttentionPushCategoryPreferences
                    .FromJson(preferences.AttentionPushCategoryPreferencesJson)
                    .Categories.TryGetValue("winner-key", out bool enabled)
                && enabled);

        Task loser = Task.Run(() => RunCategoryPatchAsync(
            host,
            userId,
            new Dictionary<string, bool> { ["loser-key"] = false },
            coordinator.LoserReadHookAsync,
            coordinator.ClassifyAfterWinnerCommit));
        Task winner = Task.Run(() => RunWinnerAsync(
            () => RunCategoryPatchAsync(
                host,
                userId,
                new Dictionary<string, bool> { ["winner-key"] = true },
                coordinator.WinnerReadHookAsync,
                classifier: null),
            coordinator));
        await Task.WhenAll(loser, winner).WaitAsync(TimeSpan.FromSeconds(30));

        await using AppDbContext verify = await factory.CreateDbContextAsync();
        NotificationPreferences persisted = await verify.NotificationPreferences
            .AsNoTracking()
            .SingleAsync(preferences => preferences.UserId == userId);
        AttentionPushCategoryPreferences map = AttentionPushCategoryPreferences.FromJson(
            persisted.AttentionPushCategoryPreferencesJson);
        map.Categories.Should().ContainKey("loser-key").WhoseValue.Should().BeFalse();
        map.Categories.Should().ContainKey("winner-key").WhoseValue.Should().BeTrue();
        coordinator.AssertExactOrderingAndRetry();
    }

    private static async Task RunPreferencePatchAsync(
        CustomWebApplicationFactory host,
        Guid userId,
        NotificationPreferencesUpdate patch,
        Func<AppDbContext, CancellationToken, Task> hook,
        Func<Exception, PreferenceConcurrencyRetry.ClassifierDecision>? classifier)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        var service = (NotificationService)scope.ServiceProvider.GetRequiredService<INotificationService>();
        service.OnAfterPreferenceReadForTestsAsync = hook;
        service.PreferenceConflictClassifierForTests = classifier;
        _ = await service.UpdatePreferencesAsync(userId, patch, CancellationToken.None);
    }

    private static async Task RunCategoryPatchAsync(
        CustomWebApplicationFactory host,
        Guid userId,
        IReadOnlyDictionary<string, bool> patch,
        Func<AppDbContext, CancellationToken, Task> hook,
        Func<Exception, PreferenceConcurrencyRetry.ClassifierDecision>? classifier)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        var service = (NotificationService)scope.ServiceProvider.GetRequiredService<INotificationService>();
        service.OnAfterPreferenceReadForTestsAsync = hook;
        service.PreferenceConflictClassifierForTests = classifier;
        AttentionCategoryUpdateResult result = await service.UpdateAttentionCategoryPreferencesAsync(
            userId,
            patch,
            CancellationToken.None);
        result.Status.Should().Be(AttentionCategoryUpdateStatus.Success);
    }

    private static async Task RunWinnerAsync(Func<Task> operation, AdverseRaceCoordinator coordinator)
    {
        try
        {
            await operation();
            coordinator.WinnerCommitted.TrySetResult();
        }
        catch (Exception exception)
        {
            coordinator.WinnerCommitted.TrySetException(exception);
            throw;
        }
    }

    private static async Task SeedUserAsync(
        IDbContextFactory<AppDbContext> factory,
        Guid userId,
        bool createPreferences)
    {
        await using AppDbContext context = await factory.CreateDbContextAsync();
        context.Users.Add(new User
        {
            Id = userId,
            Username = $"race-{userId:N}",
            Email = $"race-{userId:N}@test.local",
            PasswordHash = "x",
        });
        if (createPreferences)
        {
            context.NotificationPreferences.Add(NotificationPreferencesDefaults.Create(userId));
        }

        await context.SaveChangesAsync();
    }

    private static NotificationPreferencesUpdate MatrixPatch(
        NotificationPreferenceEvent eventType,
        bool inApp,
        bool email,
        bool push,
        bool telegram) =>
        new(
            EnableEmailNotifications: null,
            EnablePushNotifications: null,
            EnableInAppNotifications: null,
            EnableTelegramNotifications: null,
            NotifyOnStart: null,
            NotifyOnCompletion: null,
            NotifyOnFailure: null,
            NotifyOnPause: null,
            Frequency: null,
            RetentionDays: null,
            MatrixRows:
            [
                new NotificationPreferencesRowPatch(eventType, inApp, email, push, telegram),
            ]);

    private sealed class AdverseRaceCoordinator
    {
        private readonly Guid _userId;
        private readonly bool _initialRowShouldExist;
        private readonly Func<NotificationPreferences?, bool> _retryWinnerPredicate;
        private readonly TaskCompletionSource _loserRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _winnerRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _loserClassifying = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _contextIds = new();
        private int _loserReadCount;
        private int _winnerReadCount;
        private int _classifierCalls;
        private bool _retryStartedAfterWinnerCommit;
        private bool _retryObservedWinner;

        public AdverseRaceCoordinator(
            Guid userId,
            bool initialRowShouldExist,
            Func<NotificationPreferences?, bool> retryWinnerPredicate)
        {
            _userId = userId;
            _initialRowShouldExist = initialRowShouldExist;
            _retryWinnerPredicate = retryWinnerPredicate;
        }

        public TaskCompletionSource WinnerCommitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task LoserReadHookAsync(AppDbContext context, CancellationToken cancellationToken)
        {
            _contextIds.Enqueue(context.ContextId.ToString());
            int readNumber = Interlocked.Increment(ref _loserReadCount);
            NotificationPreferences? observed = await ReadAsync(context, cancellationToken);
            if (readNumber == 1)
            {
                (observed is not null).Should().Be(_initialRowShouldExist);
                _loserRead.TrySetResult();
                await _winnerRead.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                throw new DbUpdateConcurrencyException("forced losing serializable transaction");
            }

            _retryStartedAfterWinnerCommit = WinnerCommitted.Task.IsCompletedSuccessfully;
            _retryObservedWinner = _retryWinnerPredicate(observed);
        }

        public async Task WinnerReadHookAsync(AppDbContext context, CancellationToken cancellationToken)
        {
            _contextIds.Enqueue(context.ContextId.ToString());
            Interlocked.Increment(ref _winnerReadCount).Should().Be(1);
            await _loserRead.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            NotificationPreferences? observed = await ReadAsync(context, cancellationToken);
            (observed is not null).Should().Be(_initialRowShouldExist);
            _winnerRead.TrySetResult();
            await _loserClassifying.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }

        public PreferenceConcurrencyRetry.ClassifierDecision ClassifyAfterWinnerCommit(Exception exception)
        {
            Interlocked.Increment(ref _classifierCalls).Should().Be(1);
            exception.GetType().Should().Be(typeof(DbUpdateConcurrencyException));
            _loserClassifying.TrySetResult();
            WinnerCommitted.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            return PreferenceConcurrencyRetry.Classify(exception);
        }

        public void AssertExactOrderingAndRetry()
        {
            _loserReadCount.Should().Be(2, "the loser must have one initial read and one retry reread");
            _winnerReadCount.Should().Be(1, "the winner must commit without retry");
            _classifierCalls.Should().Be(1, "exactly one losing transaction must be classified");
            _contextIds.Should().HaveCount(3);
            _contextIds.Should().OnlyHaveUniqueItems("each attempt must use a fresh pooled context lease");
            _retryStartedAfterWinnerCommit.Should().BeTrue();
            _retryObservedWinner.Should().BeTrue("the retry reread must merge the competing commit");
        }

        private async Task<NotificationPreferences?> ReadAsync(
            AppDbContext context,
            CancellationToken cancellationToken)
        {
            return await context.NotificationPreferences
                .AsNoTracking()
                .SingleOrDefaultAsync(preferences => preferences.UserId == _userId, cancellationToken);
        }
    }
}
