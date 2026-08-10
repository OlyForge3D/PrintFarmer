using System.Collections.Concurrent;
using System.Data;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Notifications;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.Notifications.NativePush;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Notifications;

public sealed class NotificationServicePreferenceRaceTests
{
    [Fact]
    public async Task UpdatePreferencesAsync_ConcurrentFirstCreate_RetriesRealSnapshotConflictAndMerges()
    {
        await using SqlitePreferenceRaceStore store = await SqlitePreferenceRaceStore.CreateAsync(
            createPreferences: false);
        var coordinator = new PreferenceRaceCoordinator(
            store.UserId,
            initialRowShouldExist: false,
            retryWinnerPredicate: preferences => preferences?.TelegramOnMaintenanceDue == true);
        var loserLogger = new RecordingLogger();
        var winnerLogger = new RecordingLogger();
        await using AppDbContext loserFallback = store.CreateContext();
        await using AppDbContext winnerFallback = store.CreateContext();
        NotificationService loser = BuildService(loserFallback, store.Factory, loserLogger);
        NotificationService winner = BuildService(winnerFallback, store.Factory, winnerLogger);
        loser.OnAfterPreferenceReadForTestsAsync = coordinator.LoserReadHookAsync;
        winner.OnAfterPreferenceReadForTestsAsync = coordinator.WinnerReadHookAsync;

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

        Task loserTask = loser.UpdatePreferencesAsync(store.UserId, loserPatch);
        Task winnerTask = RunWinnerAsync(
            () => winner.UpdatePreferencesAsync(store.UserId, winnerPatch),
            coordinator);
        await Task.WhenAll(loserTask, winnerTask).WaitAsync(TimeSpan.FromSeconds(20));

        await using AppDbContext verify = store.CreateContext();
        NotificationPreferences[] rows = await verify.NotificationPreferences
            .AsNoTracking()
            .Where(preferences => preferences.UserId == store.UserId)
            .ToArrayAsync();
        rows.Should().ContainSingle();
        rows[0].EmailOnJobStarted.Should().BeTrue();
        rows[0].TelegramOnMaintenanceDue.Should().BeTrue();
        AssertRealConflictAndExactRetry(coordinator, store.Factory, loserLogger, winnerLogger, verify.ContextId);
    }

    [Fact]
    public async Task UpdateAttentionCategoryPreferencesAsync_ConcurrentDisjointUpdates_RetriesRealSnapshotConflictAndMerges()
    {
        await using SqlitePreferenceRaceStore store = await SqlitePreferenceRaceStore.CreateAsync(
            createPreferences: true);
        var coordinator = new PreferenceRaceCoordinator(
            store.UserId,
            initialRowShouldExist: true,
            retryWinnerPredicate: preferences =>
                preferences is not null
                && AttentionPushCategoryPreferences
                    .FromJson(preferences.AttentionPushCategoryPreferencesJson)
                    .Categories.TryGetValue("winner-key", out bool enabled)
                && enabled);
        var loserLogger = new RecordingLogger();
        var winnerLogger = new RecordingLogger();
        await using AppDbContext loserFallback = store.CreateContext();
        await using AppDbContext winnerFallback = store.CreateContext();
        NotificationService loser = BuildService(loserFallback, store.Factory, loserLogger);
        NotificationService winner = BuildService(winnerFallback, store.Factory, winnerLogger);
        loser.OnAfterPreferenceReadForTestsAsync = coordinator.LoserReadHookAsync;
        winner.OnAfterPreferenceReadForTestsAsync = coordinator.WinnerReadHookAsync;

        Task<AttentionCategoryUpdateResult> loserTask = loser.UpdateAttentionCategoryPreferencesAsync(
            store.UserId,
            new Dictionary<string, bool> { ["loser-key"] = false });
        Task<AttentionCategoryUpdateResult> winnerTask = RunWinnerAsync(
            () => winner.UpdateAttentionCategoryPreferencesAsync(
                store.UserId,
                new Dictionary<string, bool> { ["winner-key"] = true }),
            coordinator);
        AttentionCategoryUpdateResult[] results = await Task.WhenAll(loserTask, winnerTask)
            .WaitAsync(TimeSpan.FromSeconds(20));

        results.Should().OnlyContain(result => result.Status == AttentionCategoryUpdateStatus.Success);
        await using AppDbContext verify = store.CreateContext();
        NotificationPreferences persisted = await verify.NotificationPreferences
            .AsNoTracking()
            .SingleAsync(preferences => preferences.UserId == store.UserId);
        AttentionPushCategoryPreferences map = AttentionPushCategoryPreferences.FromJson(
            persisted.AttentionPushCategoryPreferencesJson);
        map.Categories.Should().ContainKey("loser-key").WhoseValue.Should().BeFalse();
        map.Categories.Should().ContainKey("winner-key").WhoseValue.Should().BeTrue();
        AssertRealConflictAndExactRetry(coordinator, store.Factory, loserLogger, winnerLogger, verify.ContextId);
    }

    private static void AssertRealConflictAndExactRetry(
        PreferenceRaceCoordinator coordinator,
        TrackingContextFactory factory,
        RecordingLogger loserLogger,
        RecordingLogger winnerLogger,
        DbContextId verificationContextId)
    {
        coordinator.LoserReadCount.Should().Be(2);
        coordinator.WinnerReadCount.Should().Be(1);
        coordinator.RetryStartedAfterWinnerCommit.Should().BeTrue();
        coordinator.RetryObservedWinner.Should().BeTrue();
        coordinator.ContextIds.Should().HaveCount(3);
        coordinator.ContextIds.Should().OnlyHaveUniqueItems();
        coordinator.TransactionIds.Should().HaveCount(3);
        coordinator.TransactionIds.Should().OnlyHaveUniqueItems();
        factory.CreatedContextIds.Should().HaveCount(3);
        factory.CreatedContextIds.Should().OnlyHaveUniqueItems();
        factory.CreatedContextIds.Should().BeEquivalentTo(coordinator.ContextIds);
        factory.CreatedContextIds.Should().NotContain(verificationContextId);

        winnerLogger.Exceptions.Should().BeEmpty();
        Exception conflict = loserLogger.Exceptions.Should().ContainSingle().Subject;
        DbUpdateException updateConflict = conflict.Should().BeOfType<DbUpdateException>().Subject;
        SqliteException provider = updateConflict.InnerException.Should().BeOfType<SqliteException>().Subject;
        provider.SqliteErrorCode.Should().Be(5);
        provider.SqliteExtendedErrorCode.Should().Be(517);
        provider.Message.Should().Contain("database is locked");
    }

    private static NotificationService BuildService(
        AppDbContext fallbackContext,
        TrackingContextFactory factory,
        ILogger<NotificationService> logger)
    {
        return new NotificationService(
            Mock.Of<INotificationRepository>(),
            Mock.Of<IUsersRepository>(),
            logger,
            fallbackContext,
            preferencesContextFactory: factory);
    }

    private static async Task<T> RunWinnerAsync<T>(
        Func<Task<T>> operation,
        PreferenceRaceCoordinator coordinator)
    {
        try
        {
            T result = await operation();
            coordinator.WinnerCommitted.TrySetResult();
            return result;
        }
        catch (Exception exception)
        {
            coordinator.WinnerCommitted.TrySetException(exception);
            throw;
        }
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

    private sealed class PreferenceRaceCoordinator
    {
        private readonly Guid _userId;
        private readonly bool _initialRowShouldExist;
        private readonly Func<NotificationPreferences?, bool> _retryWinnerPredicate;
        private readonly TaskCompletionSource _loserRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _winnerRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<DbContextId> _contextIds = new();
        private readonly ConcurrentQueue<Guid> _transactionIds = new();
        private int _loserReadCount;
        private int _winnerReadCount;

        public PreferenceRaceCoordinator(
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

        public int LoserReadCount => Volatile.Read(ref _loserReadCount);

        public int WinnerReadCount => Volatile.Read(ref _winnerReadCount);

        public bool RetryStartedAfterWinnerCommit { get; private set; }

        public bool RetryObservedWinner { get; private set; }

        public IReadOnlyCollection<DbContextId> ContextIds => _contextIds.ToArray();

        public IReadOnlyCollection<Guid> TransactionIds => _transactionIds.ToArray();

        public async Task LoserReadHookAsync(
            AppDbContext context,
            CancellationToken cancellationToken)
        {
            RecordSerializableAttempt(context);
            int readNumber = Interlocked.Increment(ref _loserReadCount);
            NotificationPreferences? observed = await ReadAsync(context, cancellationToken);
            if (readNumber == 1)
            {
                (observed is not null).Should().Be(_initialRowShouldExist);
                _loserRead.TrySetResult();
                await _winnerRead.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                await WinnerCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                return;
            }

            readNumber.Should().Be(2);
            RetryStartedAfterWinnerCommit = WinnerCommitted.Task.IsCompletedSuccessfully;
            RetryObservedWinner = _retryWinnerPredicate(observed);
        }

        public async Task WinnerReadHookAsync(
            AppDbContext context,
            CancellationToken cancellationToken)
        {
            RecordSerializableAttempt(context);
            Interlocked.Increment(ref _winnerReadCount).Should().Be(1);
            await _loserRead.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            NotificationPreferences? observed = await ReadAsync(context, cancellationToken);
            (observed is not null).Should().Be(_initialRowShouldExist);
            _winnerRead.TrySetResult();
        }

        private void RecordSerializableAttempt(AppDbContext context)
        {
            IDbContextTransaction? currentTransaction = context.Database.CurrentTransaction;
            currentTransaction.Should().NotBeNull(
                "each race attempt must own a relational transaction");
            IDbContextTransaction transaction = currentTransaction!;
            transaction.GetDbTransaction().IsolationLevel.Should().Be(IsolationLevel.Serializable);
            _contextIds.Enqueue(context.ContextId);
            _transactionIds.Enqueue(transaction.TransactionId);
        }

        private async Task<NotificationPreferences?> ReadAsync(
            AppDbContext context,
            CancellationToken cancellationToken)
        {
            return await context.NotificationPreferences
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    preferences => preferences.UserId == _userId,
                    cancellationToken);
        }
    }

    private sealed class TrackingContextFactory(
        DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        private readonly ConcurrentQueue<DbContextId> _createdContextIds = new();

        public IReadOnlyCollection<DbContextId> CreatedContextIds => _createdContextIds.ToArray();

        public AppDbContext CreateDbContext()
        {
            var context = new AppDbContext(options);
            _createdContextIds.Enqueue(context.ContextId);
            return context;
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    private sealed class SqlitePreferenceRaceStore : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly DbContextOptions<AppDbContext> _options;

        private SqlitePreferenceRaceStore(
            string databasePath,
            DbContextOptions<AppDbContext> options,
            Guid userId)
        {
            _databasePath = databasePath;
            _options = options;
            UserId = userId;
            Factory = new TrackingContextFactory(options);
        }

        public Guid UserId { get; }

        public TrackingContextFactory Factory { get; }

        public static async Task<SqlitePreferenceRaceStore> CreateAsync(bool createPreferences)
        {
            string databasePath = Path.Join(
                Path.GetTempPath(),
                $"preference-race-{Guid.NewGuid():N}.db");
            string connectionString =
                $"Data Source={databasePath};Pooling=False;Default Timeout=1";
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;
            Guid userId = Guid.NewGuid();
            var store = new SqlitePreferenceRaceStore(databasePath, options, userId);

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL;";
                object? mode = await command.ExecuteScalarAsync();
                mode.Should().Be("wal");
            }

            await using AppDbContext seed = store.CreateContext();
            await seed.Database.EnsureCreatedAsync();
            seed.Users.Add(new User
            {
                Id = userId,
                Username = $"preference-race-{userId:N}",
                Email = $"preference-race-{userId:N}@test.local",
                PasswordHash = "x",
            });
            if (createPreferences)
            {
                seed.NotificationPreferences.Add(NotificationPreferencesDefaults.Create(userId));
            }

            await seed.SaveChangesAsync();
            return store;
        }

        public AppDbContext CreateContext() => new(_options);

        public ValueTask DisposeAsync()
        {
            File.Delete(_databasePath);
            File.Delete(_databasePath + "-shm");
            File.Delete(_databasePath + "-wal");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLogger : ILogger<NotificationService>
    {
        private readonly ConcurrentQueue<Exception> _exceptions = new();

        public IReadOnlyCollection<Exception> Exceptions => _exceptions.ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                _exceptions.Enqueue(exception);
            }
        }
    }
}
