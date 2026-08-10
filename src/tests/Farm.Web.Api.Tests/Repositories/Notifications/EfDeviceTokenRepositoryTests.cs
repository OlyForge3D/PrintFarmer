using System.Collections.Concurrent;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Notifications;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Farm.Web.Api.Tests.Repositories.Notifications;

/// <summary>
/// Relational SQLite behavioral coverage for <see cref="EfDeviceTokenRepository"/>.
/// Provider-parity checks for PostgreSQL and SQL Server are covered by the migrations pipeline
/// and gated by the CI has-pending-model-changes checks.
/// </summary>
public sealed class EfDeviceTokenRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly EfDeviceTokenRepository _repo;

    public EfDeviceTokenRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        _connection.Open();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _repo = new EfDeviceTokenRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Upsert_CreatesNewRow_WhenInstallationDoesNotExist()
    {
        Guid userId = Guid.NewGuid();

        DeviceToken row = await _repo.UpsertAsync(userId, "install-a", "token-1", "ios", "production", "com.example.app");

        row.UserId.Should().Be(userId);
        row.InstallationId.Should().Be("install-a");
        row.Token.Should().Be("token-1");
        row.IsActive.Should().BeTrue();
        row.ConsecutiveFailureCount.Should().Be(0);
        row.RegistrationVersion.Should().Be(1);

        (await _db.DeviceTokens.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Upsert_ReplacesRow_WhenInstallationExists()
    {
        Guid userId = Guid.NewGuid();
        DeviceToken original = await _repo.UpsertAsync(
            userId,
            "install-a",
            "token-1",
            "ios",
            "production",
            "com.example.app");
        Guid originalId = original.Id;
        long originalVersion = original.RegistrationVersion;

        DeviceToken updated = await _repo.UpsertAsync(
            userId,
            "install-a",
            "token-2",
            "ios",
            "development",
            "com.example.app");

        updated.Id.Should().Be(originalId);
        updated.RegistrationVersion.Should().Be(originalVersion + 1);
        updated.Token.Should().Be("token-2");
        updated.Environment.Should().Be("development");
        (await _db.DeviceTokens.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Upsert_AfterFailureDeactivation_CreatesNewActiveIncarnation()
    {
        Guid userId = Guid.NewGuid();
        DeviceToken row = await _repo.UpsertAsync(userId, "install-a", "token-1", "ios", "production", null);
        long failedVersion = row.RegistrationVersion;
        await _repo.RecordFailureAsync(row.Id, failedVersion, DateTime.UtcNow, failureThreshold: 1);

        DeviceToken active = await _repo.UpsertAsync(userId, "install-a", "token-1", "ios", "production", null);

        active.Id.Should().NotBe(row.Id);
        active.RegistrationVersion.Should().Be(1);
        active.IsActive.Should().BeTrue();
        active.ConsecutiveFailureCount.Should().Be(0);
        active.LastFailureAt.Should().BeNull();
        DeviceToken history = await _db.DeviceTokens.AsNoTracking().SingleAsync(token => token.Id == row.Id);
        history.RegistrationVersion.Should().Be(failedVersion);
        history.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Upsert_InactiveHistoryExists_CreatesNewActiveOwnerWithoutMutatingHistory()
    {
        _ = await _db.Database.ExecuteSqlRawAsync(
            "DROP INDEX \"IX_DeviceTokens_InstallationId\";");
        _ = await _db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX \"IX_DeviceTokens_InstallationId\" "
                + "ON \"DeviceTokens\" (\"InstallationId\") WHERE \"IsActive\" = 1;");
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        Guid historyA = Guid.NewGuid();
        Guid historyB = Guid.NewGuid();
        const string installationId = "installation-with-history";
        _db.DeviceTokens.AddRange(
            new DeviceToken
            {
                Id = historyA,
                UserId = userA,
                RegistrationVersion = 7,
                InstallationId = installationId,
                Token = new string('a', 64),
                Platform = "ios",
                Environment = "production",
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                IsActive = false,
            },
            new DeviceToken
            {
                Id = historyB,
                UserId = userB,
                RegistrationVersion = 9,
                InstallationId = installationId,
                Token = new string('b', 64),
                Platform = "ios",
                Environment = "production",
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                IsActive = false,
            });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        DeviceToken active = await _repo.UpsertAsync(
            userB,
            installationId,
            new string('c', 64),
            "ios",
            "production",
            "com.example.app");

        DeviceToken[] rows = await _db.DeviceTokens
            .AsNoTracking()
            .Where(token => token.InstallationId == installationId)
            .ToArrayAsync();
        rows.Should().HaveCount(3);
        rows.Where(token => !token.IsActive)
            .Select(token => token.Id)
            .Should().BeEquivalentTo([historyA, historyB]);
        rows.Should().ContainSingle(token =>
            token.Id == active.Id
            && token.UserId == userB
            && token.IsActive
            && token.RegistrationVersion == 1);
    }

    [Fact]
    public async Task Delete_RemovesOnlyMatchingRow()
    {
        Guid userId = Guid.NewGuid();
        await _repo.UpsertAsync(userId, "install-a", "token-a", "ios", "production", null);
        await _repo.UpsertAsync(userId, "install-b", "token-b", "ios", "production", null);

        bool removed = await _repo.DeleteByInstallationAsync(userId, "install-a");

        removed.Should().BeTrue();
        (await _db.DeviceTokens.CountAsync()).Should().Be(1);
        (await _db.DeviceTokens.SingleAsync()).InstallationId.Should().Be("install-b");
    }

    [Fact]
    public async Task GetActiveByUser_OnlyReturnsActiveRows()
    {
        Guid userId = Guid.NewGuid();
        DeviceToken active = await _repo.UpsertAsync(userId, "install-a", "token-a", "ios", "production", null);
        DeviceToken inactive = await _repo.UpsertAsync(userId, "install-b", "token-b", "ios", "production", null);
        inactive.IsActive = false;
        await _db.SaveChangesAsync();

        IReadOnlyList<DeviceToken> result = await _repo.GetActiveByUserAsync(userId);

        result.Should().ContainSingle(t => t.Id == active.Id);
    }

    [Fact]
    public async Task RecordFailure_DeactivatesAfterThreshold()
    {
        Guid userId = Guid.NewGuid();
        DeviceToken row = await _repo.UpsertAsync(userId, "install-a", "token-a", "ios", "production", null);

        for (int i = 0; i < 5; i++)
        {
            await _repo.RecordFailureAsync(row.Id, row.RegistrationVersion, DateTime.UtcNow, failureThreshold: 5);
        }

        DeviceToken? refreshed = await _db.DeviceTokens.AsNoTracking().FirstAsync(t => t.Id == row.Id);
        refreshed.ConsecutiveFailureCount.Should().Be(5);
        refreshed.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task RecordSuccess_ResetsFailureCounters()
    {
        Guid userId = Guid.NewGuid();
        DeviceToken row = await _repo.UpsertAsync(userId, "install-a", "token-a", "ios", "production", null);
        await _repo.RecordFailureAsync(row.Id, row.RegistrationVersion, DateTime.UtcNow, failureThreshold: 10);
        await _repo.RecordFailureAsync(row.Id, row.RegistrationVersion, DateTime.UtcNow, failureThreshold: 10);

        await _repo.RecordSuccessAsync(row.Id, row.RegistrationVersion, DateTime.UtcNow);

        DeviceToken? refreshed = await _db.DeviceTokens.AsNoTracking().FirstAsync(t => t.Id == row.Id);
        refreshed.ConsecutiveFailureCount.Should().Be(0);
        refreshed.LastFailureAt.Should().BeNull();
    }

    [Fact]
    public async Task RecordSuccess_StaleVersionAfterSurvivingRotation_IsNoOpAndCurrentVersionApplies()
    {
        (DeviceToken original, DeviceToken rotated) = await SeedSurvivingRotationAsync();
        DateTime existingFailureAt = new(2031, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await _repo.RecordFailureAsync(
            rotated.Id,
            rotated.RegistrationVersion,
            existingFailureAt,
            failureThreshold: 3);
        DeviceToken baseline = await ReadPersistedAsync(rotated.Id);
        baseline.ConsecutiveFailureCount.Should().Be(1);
        baseline.LastFailureAt.Should().Be(existingFailureAt);
        baseline.IsActive.Should().BeTrue();

        DateTime staleSuccessAt = new(2032, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        await _repo.RecordSuccessAsync(
            original.Id,
            original.RegistrationVersion,
            staleSuccessAt);

        DeviceToken afterStale = await ReadPersistedAsync(rotated.Id);
        AssertPersistedStateUnchanged(afterStale, baseline);

        DateTime currentSuccessAt = new(2033, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        await _repo.RecordSuccessAsync(
            rotated.Id,
            rotated.RegistrationVersion,
            currentSuccessAt);

        DeviceToken afterCurrent = await ReadPersistedAsync(rotated.Id);
        afterCurrent.RegistrationVersion.Should().Be(rotated.RegistrationVersion);
        afterCurrent.Token.Should().Be(rotated.Token);
        afterCurrent.IsActive.Should().BeTrue();
        afterCurrent.ConsecutiveFailureCount.Should().Be(0);
        afterCurrent.LastFailureAt.Should().BeNull();
        afterCurrent.LastUsedAt.Should().Be(currentSuccessAt);
    }

    [Fact]
    public async Task RecordFailure_StaleVersionAfterSurvivingRotation_IsNoOpAndCurrentVersionApplies()
    {
        (DeviceToken original, DeviceToken rotated) = await SeedSurvivingRotationAsync();
        DeviceToken baseline = await ReadPersistedAsync(rotated.Id);
        DateTime staleFailureAt = new(2032, 4, 5, 6, 7, 8, DateTimeKind.Utc);

        await _repo.RecordFailureAsync(
            original.Id,
            original.RegistrationVersion,
            staleFailureAt,
            failureThreshold: 2);

        DeviceToken afterStale = await ReadPersistedAsync(rotated.Id);
        AssertPersistedStateUnchanged(afterStale, baseline);

        DateTime currentFailureAt = new(2033, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        await _repo.RecordFailureAsync(
            rotated.Id,
            rotated.RegistrationVersion,
            currentFailureAt,
            failureThreshold: 2);

        DeviceToken afterCurrent = await ReadPersistedAsync(rotated.Id);
        afterCurrent.RegistrationVersion.Should().Be(rotated.RegistrationVersion);
        afterCurrent.Token.Should().Be(rotated.Token);
        afterCurrent.LastUsedAt.Should().Be(baseline.LastUsedAt);
        afterCurrent.IsActive.Should().BeTrue();
        afterCurrent.ConsecutiveFailureCount.Should().Be(1);
        afterCurrent.LastFailureAt.Should().Be(currentFailureAt);
    }

    [Fact]
    public async Task Invalidate_StaleVersionAfterSurvivingRotation_ReturnsFalseAndCurrentVersionDeletes()
    {
        (DeviceToken original, DeviceToken rotated) = await SeedSurvivingRotationAsync();
        DeviceToken baseline = await ReadPersistedAsync(rotated.Id);

        bool staleRemoved = await _repo.InvalidateAsync(
            original.Id,
            original.RegistrationVersion);

        staleRemoved.Should().BeFalse();
        DeviceToken afterStale = await ReadPersistedAsync(rotated.Id);
        AssertPersistedStateUnchanged(afterStale, baseline);
        afterStale.IsActive.Should().BeTrue();

        bool currentRemoved = await _repo.InvalidateAsync(
            rotated.Id,
            rotated.RegistrationVersion);

        currentRemoved.Should().BeTrue();
        await using AppDbContext verify = CreateSiblingContext();
        (await verify.DeviceTokens.AsNoTracking().CountAsync(token => token.Id == rotated.Id))
            .Should().Be(0);
    }

    [Fact]
    public async Task Invalidate_ExactRegistration_PreservesDuplicateTokenAcrossProviderScopes()
    {
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        DeviceToken sandbox = await _repo.UpsertAsync(
            userA,
            "install-sandbox",
            "shared-token",
            "ios",
            "development",
            "com.example.sandbox");
        DeviceToken sameUserSameProviderScope = await _repo.UpsertAsync(
            userA,
            "install-sandbox-2",
            "shared-token",
            "ios",
            "development",
            "com.example.sandbox");
        DeviceToken production = await _repo.UpsertAsync(
            userA,
            "install-production",
            "shared-token",
            "ios",
            "production",
            "com.example.production");
        DeviceToken otherUser = await _repo.UpsertAsync(
            userB,
            "install-other-user",
            "shared-token",
            "ios",
            "development",
            "com.example.sandbox");
        DeviceToken otherToken = await _repo.UpsertAsync(
            userB,
            "install-other-token",
            "other-token",
            "ios",
            "production",
            "com.example.production");

        bool removed = await _repo.InvalidateAsync(sandbox.Id, sandbox.RegistrationVersion);

        removed.Should().BeTrue();
        DeviceToken[] remaining = await _db.DeviceTokens.AsNoTracking().ToArrayAsync();
        remaining.Select(token => token.Id).Should().BeEquivalentTo(
            new[] { sameUserSameProviderScope.Id, production.Id, otherUser.Id, otherToken.Id });
        remaining.Count(token => token.Token == "shared-token").Should().Be(3);
        remaining.Should().ContainSingle(token =>
            token.Id == sameUserSameProviderScope.Id
            && token.UserId == sandbox.UserId
            && token.Environment == sandbox.Environment
            && token.AppBundleId == sandbox.AppBundleId);
    }

    [Fact]
    public async Task GetActiveTokenOwners_ReturnsDistinctActiveUserIds()
    {
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        await _repo.UpsertAsync(userA, "i1", "t1", "ios", "production", null);
        await _repo.UpsertAsync(userA, "i2", "t2", "ios", "production", null);
        await _repo.UpsertAsync(userB, "i3", "t3", "ios", "production", null);

        IReadOnlyList<Guid> owners = await _repo.GetActiveTokenOwnersAsync();

        owners.Should().BeEquivalentTo(new[] { userA, userB });
    }

    [Fact]
    public async Task Upsert_ConcurrentFirstCreate_RealUniqueViolationRetriesExactlyOnce()
    {
        string databasePath = Path.Join(
            Path.GetTempPath(),
            $"device-token-race-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={databasePath};Pooling=False;Default Timeout=5";
        DbContextOptions<AppDbContext> plainOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var interceptor = new ConcurrentInsertBarrierInterceptor();
        DbContextOptions<AppDbContext> racingOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(interceptor)
            .Options;
        Guid userId = Guid.NewGuid();

        try
        {
            await using (AppDbContext seed = new(plainOptions))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Users.Add(new Farm.Infrastructure.Domain.User
                {
                    Id = userId,
                    Username = $"device-token-race-{userId:N}",
                    Email = $"device-token-race-{userId:N}@example.com",
                    PasswordHash = "x",
                });
                await seed.SaveChangesAsync();
            }

            await using AppDbContext contextA = new(racingOptions);
            await using AppDbContext contextB = new(racingOptions);
            var repositoryA = new EfDeviceTokenRepository(contextA);
            var repositoryB = new EfDeviceTokenRepository(contextB);

            Task<DeviceToken> writeA = repositoryA.UpsertAsync(
                userId,
                "installation-race",
                new string('a', 64),
                "ios",
                "production",
                "com.example.app");
            Task<DeviceToken> writeB = repositoryB.UpsertAsync(
                userId,
                "installation-race",
                new string('b', 64),
                "ios",
                "production",
                "com.example.app");

            await interceptor.BothInitialInsertsReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            DeviceToken[] results = await Task.WhenAll(writeA, writeB).WaitAsync(TimeSpan.FromSeconds(10));

            results.Should().HaveCount(2);
            interceptor.SaveAttemptsByContext.Should().HaveCount(2);
            interceptor.SaveAttemptsByContext.Values.Order().Should().Equal(1, 2);
            interceptor.Failures.Should().ContainSingle();
            DbUpdateException conflict = interceptor.Failures.Single().Should().BeOfType<DbUpdateException>().Subject;
            SqliteException provider = conflict.InnerException.Should().BeOfType<SqliteException>().Subject;
            provider.SqliteErrorCode.Should().Be(19);
            provider.SqliteExtendedErrorCode.Should().Be(2067);
            provider.Message.Should().Contain(
                "UNIQUE constraint failed: DeviceTokens.InstallationId");

            await using AppDbContext verify = new(plainOptions);
            DeviceToken persisted = await verify.DeviceTokens.AsNoTracking().SingleAsync();
            persisted.UserId.Should().Be(userId);
            persisted.InstallationId.Should().Be("installation-race");
            persisted.Token.Should().BeOneOf(new string('a', 64), new string('b', 64));
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task Upsert_ConcurrentCrossUserFirstClaim_NoIncumbent_ConvergesToOneActiveOwner()
    {
        string databasePath = Path.Join(
            Path.GetTempPath(),
            $"device-token-owner-race-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={databasePath};Pooling=False;Default Timeout=5";
        DbContextOptions<AppDbContext> plainOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var interceptor = new ConcurrentInsertBarrierInterceptor();
        DbContextOptions<AppDbContext> racingOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(interceptor)
            .Options;
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();

        try
        {
            await using (AppDbContext seed = new(plainOptions))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Users.AddRange(
                    new Farm.Infrastructure.Domain.User
                    {
                        Id = userA,
                        Username = $"device-token-owner-a-{userA:N}",
                        Email = $"device-token-owner-a-{userA:N}@example.com",
                        PasswordHash = "x",
                    },
                    new Farm.Infrastructure.Domain.User
                    {
                        Id = userB,
                        Username = $"device-token-owner-b-{userB:N}",
                        Email = $"device-token-owner-b-{userB:N}@example.com",
                        PasswordHash = "x",
                    });
                await seed.SaveChangesAsync();
            }

            await using AppDbContext contextA = new(racingOptions);
            await using AppDbContext contextB = new(racingOptions);
            var repositoryA = new EfDeviceTokenRepository(contextA);
            var repositoryB = new EfDeviceTokenRepository(contextB);

            Task<DeviceToken> claimA = repositoryA.UpsertAsync(
                userA,
                "installation-owner-race",
                new string('a', 64),
                "ios",
                "production",
                "com.example.app");
            Task<DeviceToken> claimB = repositoryB.UpsertAsync(
                userB,
                "installation-owner-race",
                new string('b', 64),
                "ios",
                "production",
                "com.example.app");

            await interceptor.BothInitialInsertsReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            DeviceToken[] results = await Task.WhenAll(claimA, claimB).WaitAsync(TimeSpan.FromSeconds(10));

            results.Should().HaveCount(2);
            interceptor.Failures.Should().ContainSingle(
                "the database owner index must serialize concurrent first claims");
            await using AppDbContext verify = new(plainOptions);
            DeviceToken[] rows = await verify.DeviceTokens.AsNoTracking().ToArrayAsync();
            DeviceToken persisted = rows.Should().ContainSingle(token => token.IsActive).Subject;
            rows.Should().ContainSingle();
            persisted.InstallationId.Should().Be("installation-owner-race");
            new[] { userA, userB }.Should().Contain(persisted.UserId);
            persisted.IsActive.Should().BeTrue();
            persisted.RegistrationVersion.Should().Be(2);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task Upsert_ConcurrentRefresh_RetriesAndRotatesVersionForEachSuccessfulRegistration()
    {
        string databasePath = Path.Join(
            Path.GetTempPath(),
            $"device-token-refresh-race-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={databasePath};Pooling=False;Default Timeout=5";
        DbContextOptions<AppDbContext> plainOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var interceptor = new PauseFirstDeviceTokenUpdateInterceptor();
        DbContextOptions<AppDbContext> pausedOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(interceptor)
            .Options;
        Guid userId = Guid.NewGuid();

        try
        {
            DeviceToken original;
            await using (AppDbContext seed = new(plainOptions))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Users.Add(new Farm.Infrastructure.Domain.User
                {
                    Id = userId,
                    Username = $"device-token-refresh-{userId:N}",
                    Email = $"device-token-refresh-{userId:N}@example.com",
                    PasswordHash = "x",
                });
                await seed.SaveChangesAsync();
                original = await new EfDeviceTokenRepository(seed).UpsertAsync(
                    userId,
                    "installation-refresh-race",
                    new string('0', 64),
                    "ios",
                    "production",
                    "com.example.original");
            }

            await using AppDbContext contextA = new(pausedOptions);
            await using AppDbContext contextB = new(plainOptions);
            var repositoryA = new EfDeviceTokenRepository(contextA);
            var repositoryB = new EfDeviceTokenRepository(contextB);

            Task<DeviceToken> writeA = repositoryA.UpsertAsync(
                userId,
                "installation-refresh-race",
                new string('a', 64),
                "ios",
                "development",
                "com.example.a");
            await interceptor.FirstUpdateReady.Task.WaitAsync(TimeSpan.FromSeconds(10));

            DeviceToken writeB;
            try
            {
                writeB = await repositoryB.UpsertAsync(
                    userId,
                    "installation-refresh-race",
                    new string('b', 64),
                    "ios",
                    "production",
                    "com.example.b");
                interceptor.ReleaseFirstUpdate.TrySetResult();
            }
            catch (Exception exception)
            {
                interceptor.ReleaseFirstUpdate.TrySetException(exception);
                throw;
            }

            DeviceToken writeAResult = await writeA.WaitAsync(TimeSpan.FromSeconds(10));

            writeB.RegistrationVersion.Should().Be(original.RegistrationVersion + 1);
            writeAResult.RegistrationVersion.Should().Be(original.RegistrationVersion + 2);
            interceptor.SaveAttempts.Should().Be(2);
            interceptor.Failures.Should().ContainSingle()
                .Which.Should().BeOfType<DbUpdateConcurrencyException>();

            await using AppDbContext verify = new(plainOptions);
            DeviceToken persisted = await verify.DeviceTokens.AsNoTracking().SingleAsync();
            persisted.Id.Should().Be(original.Id);
            persisted.RegistrationVersion.Should().Be(original.RegistrationVersion + 2);
            persisted.Token.Should().Be(new string('a', 64));
            persisted.Environment.Should().Be("development");
            persisted.AppBundleId.Should().Be("com.example.a");
            persisted.IsActive.Should().BeTrue();
            persisted.ConsecutiveFailureCount.Should().Be(0);
        }
        finally
        {
            interceptor.ReleaseFirstUpdate.TrySetCanceled();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task Upsert_NonUniqueDbUpdateException_IsNotRetried()
    {
        var original = new DbUpdateException(
            "foreign key violation",
            new SqliteException("FOREIGN KEY constraint failed", errorCode: 19, extendedErrorCode: 787));
        var interceptor = new ThrowingSaveInterceptor(original);
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;
        await using AppDbContext context = new(options);
        var repository = new EfDeviceTokenRepository(context);

        Func<Task> act = () => repository.UpsertAsync(
            Guid.NewGuid(),
            "installation-fk",
            new string('c', 64),
            "ios",
            "production",
            "com.example.app");

        (await act.Should().ThrowAsync<DbUpdateException>())
            .Which.Should().BeSameAs(original);
        interceptor.InvocationCount.Should().Be(1, "non-unique failures must not consume the retry budget");
    }

    [Theory]
    [InlineData("UNIQUE constraint failed: DeviceTokens.InstallationId", true)]
    [InlineData("UNIQUE constraint failed: DeviceTokens.Token", false)]
    [InlineData("UNIQUE constraint failed: DeviceTokens.UserId, DeviceTokens.InstallationId, DeviceTokens.Token", false)]
    [InlineData("UNIQUE constraint failed: Other.UserId, Other.InstallationId", false)]
    public void IsUniqueDeviceTokenConflict_OnlyAcceptsExactUpsertKey(string message, bool expected)
    {
        var exception = new DbUpdateException(
            "save failed",
            new SqliteException(message, errorCode: 19, extendedErrorCode: 2067));

        EfDeviceTokenRepository.IsUniqueDeviceTokenConflict(exception).Should().Be(expected);
    }

    [Theory]
    [InlineData("IX_DeviceTokens_InstallationId", true)]
    [InlineData("ix_devicetokens_installationid", false)]
    [InlineData("IX_DeviceTokens_InstallationId_Extra", false)]
    public void IsUniqueDeviceTokenConflict_PostgresRequiresExactConstraintName(
        string constraintName,
        bool expected)
    {
        var exception = new DbUpdateException(
            "save failed",
            new PostgresException(
                "unique violation",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation,
                constraintName: constraintName));

        EfDeviceTokenRepository.IsUniqueDeviceTokenConflict(exception).Should().Be(expected);
    }

    private async Task<(DeviceToken Original, DeviceToken Rotated)> SeedSurvivingRotationAsync()
    {
        Guid userId = Guid.NewGuid();
        string installationId = $"surviving-rotation-{Guid.NewGuid():N}";
        DeviceToken original = await _repo.UpsertAsync(
            userId,
            installationId,
            new string('a', 64),
            "ios",
            "production",
            "com.example.original");

        await using AppDbContext refreshContext = CreateSiblingContext();
        var refreshRepository = new EfDeviceTokenRepository(refreshContext);
        DeviceToken rotated = await refreshRepository.UpsertAsync(
            userId,
            installationId,
            new string('b', 64),
            "ios",
            "development",
            "com.example.rotated");

        rotated.Id.Should().Be(original.Id, "registration refresh updates the surviving row");
        rotated.RegistrationVersion.Should().Be(original.RegistrationVersion + 1);
        rotated.IsActive.Should().BeTrue();
        return (original, rotated);
    }

    private AppDbContext CreateSiblingContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new AppDbContext(options);
    }

    private async Task<DeviceToken> ReadPersistedAsync(Guid id)
    {
        await using AppDbContext read = CreateSiblingContext();
        return await read.DeviceTokens
            .AsNoTracking()
            .SingleAsync(token => token.Id == id);
    }

    private static void AssertPersistedStateUnchanged(DeviceToken actual, DeviceToken expected)
    {
        actual.Id.Should().Be(expected.Id);
        actual.RegistrationVersion.Should().Be(expected.RegistrationVersion);
        actual.UserId.Should().Be(expected.UserId);
        actual.InstallationId.Should().Be(expected.InstallationId);
        actual.Token.Should().Be(expected.Token);
        actual.Platform.Should().Be(expected.Platform);
        actual.Environment.Should().Be(expected.Environment);
        actual.AppBundleId.Should().Be(expected.AppBundleId);
        actual.CreatedAt.Should().Be(expected.CreatedAt);
        actual.LastUsedAt.Should().Be(expected.LastUsedAt);
        actual.LastFailureAt.Should().Be(expected.LastFailureAt);
        actual.ConsecutiveFailureCount.Should().Be(expected.ConsecutiveFailureCount);
        actual.IsActive.Should().Be(expected.IsActive);
    }

    private sealed class ConcurrentInsertBarrierInterceptor : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _winnerSaved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _initialInsertArrivals;
        private DbContextId? _winnerContextId;

        public TaskCompletionSource BothInitialInsertsReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentDictionary<DbContextId, int> SaveAttemptsByContext { get; } = new();

        public ConcurrentQueue<Exception> Failures { get; } = new();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            AppDbContext context = (AppDbContext)eventData.Context!;
            bool writesDeviceToken = context.ChangeTracker.Entries<DeviceToken>()
                .Any(entry => entry.State is EntityState.Added or EntityState.Modified);
            if (!writesDeviceToken)
            {
                return result;
            }

            SaveAttemptsByContext.AddOrUpdate(context.ContextId, 1, (_, count) => count + 1);
            bool initialInsert = context.ChangeTracker.Entries<DeviceToken>()
                .Any(entry => entry.State == EntityState.Added);
            if (!initialInsert)
            {
                return result;
            }

            int arrival = Interlocked.Increment(ref _initialInsertArrivals);
            if (arrival == 1)
            {
                _winnerContextId = context.ContextId;
                await BothInitialInsertsReached.Task.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    cancellationToken);
            }
            else if (arrival == 2)
            {
                BothInitialInsertsReached.TrySetResult();
                await _winnerSaved.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            else
            {
                throw new InvalidOperationException("Only two initial insert attempts are expected.");
            }

            return result;
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (_winnerContextId == eventData.Context!.ContextId)
            {
                _winnerSaved.TrySetResult();
            }

            return ValueTask.FromResult(result);
        }

        public override Task SaveChangesFailedAsync(
            DbContextErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Failures.Enqueue(eventData.Exception);
            return Task.CompletedTask;
        }
    }

    private sealed class PauseFirstDeviceTokenUpdateInterceptor : SaveChangesInterceptor
    {
        private int _firstUpdateObserved;

        public TaskCompletionSource FirstUpdateReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstUpdate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<Exception> Failures { get; } = new();

        public int SaveAttempts { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool updatesRegistration = eventData.Context!.ChangeTracker.Entries<DeviceToken>()
                .Any(entry => entry.State == EntityState.Modified);
            if (!updatesRegistration)
            {
                return result;
            }

            SaveAttempts++;
            if (Interlocked.CompareExchange(ref _firstUpdateObserved, 1, 0) == 0)
            {
                FirstUpdateReady.TrySetResult();
                await ReleaseFirstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }

        public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
            ConcurrencyExceptionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Failures.Enqueue(eventData.Exception);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowingSaveInterceptor(DbUpdateException exception) : SaveChangesInterceptor
    {
        public int InvocationCount { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            throw exception;
        }
    }
}
