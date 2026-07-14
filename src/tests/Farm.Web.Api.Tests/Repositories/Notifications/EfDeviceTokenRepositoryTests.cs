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
/// Behavioral coverage for <see cref="EfDeviceTokenRepository"/> using EF Core's InMemory
/// provider. Provider-parity checks (unique index behaviour under PG/SQL Server) are covered
/// by the relational migrations pipeline and gated by the CI has-pending-model-changes check.
/// </summary>
public sealed class EfDeviceTokenRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly EfDeviceTokenRepository _repo;

    public EfDeviceTokenRepositoryTests()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new EfDeviceTokenRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
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

        (await _db.DeviceTokens.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Upsert_ReplacesRow_WhenInstallationExists()
    {
        Guid userId = Guid.NewGuid();
        await _repo.UpsertAsync(userId, "install-a", "token-1", "ios", "production", "com.example.app");

        DeviceToken updated = await _repo.UpsertAsync(userId, "install-a", "token-2", "ios", "development", "com.example.app");

        updated.Token.Should().Be("token-2");
        updated.Environment.Should().Be("development");
        (await _db.DeviceTokens.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Upsert_ReactivatesRow_AndResetsFailures()
    {
        Guid userId = Guid.NewGuid();
        DeviceToken row = await _repo.UpsertAsync(userId, "install-a", "token-1", "ios", "production", null);
        await _repo.RecordFailureAsync(row.Id, DateTime.UtcNow, failureThreshold: 1);

        DeviceToken reactivated = await _repo.UpsertAsync(userId, "install-a", "token-1", "ios", "production", null);

        reactivated.IsActive.Should().BeTrue();
        reactivated.ConsecutiveFailureCount.Should().Be(0);
        reactivated.LastFailureAt.Should().BeNull();
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
            await _repo.RecordFailureAsync(row.Id, DateTime.UtcNow, failureThreshold: 5);
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
        await _repo.RecordFailureAsync(row.Id, DateTime.UtcNow, failureThreshold: 10);
        await _repo.RecordFailureAsync(row.Id, DateTime.UtcNow, failureThreshold: 10);

        await _repo.RecordSuccessAsync(row.Id, DateTime.UtcNow);

        DeviceToken? refreshed = await _db.DeviceTokens.AsNoTracking().FirstAsync(t => t.Id == row.Id);
        refreshed.ConsecutiveFailureCount.Should().Be(0);
        refreshed.LastFailureAt.Should().BeNull();
    }

    [Fact]
    public async Task InvalidateByToken_RemovesAllMatchingRows()
    {
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        await _repo.UpsertAsync(userA, "install-a", "shared-token", "ios", "production", null);
        await _repo.UpsertAsync(userB, "install-b", "shared-token", "ios", "production", null);
        await _repo.UpsertAsync(userB, "install-c", "other-token", "ios", "production", null);

        int removed = await _repo.InvalidateByTokenAsync("shared-token");

        removed.Should().Be(2);
        (await _db.DeviceTokens.CountAsync()).Should().Be(1);
        (await _db.DeviceTokens.SingleAsync()).Token.Should().Be("other-token");
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
    public async Task Upsert_UniqueInstallationCollision_RetriesAsIdempotentUpdate()
    {
        const string connectionString = "Data Source=file:device-token-unique-race?mode=memory&cache=shared";
        await using SqliteConnection keepAlive = new(connectionString);
        await keepAlive.OpenAsync();

        DbContextOptions<AppDbContext> plainOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        Guid userId = Guid.NewGuid();
        await using (AppDbContext seed = new(plainOptions))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Users.Add(new Farm.Infrastructure.Domain.User
            {
                Id = userId,
                Username = "device-token-race",
                Email = "device-token-race@example.com",
                PasswordHash = "x",
            });
            await seed.SaveChangesAsync();
        }

        var interceptor = new CollisionInterceptor(
            plainOptions,
            userId,
            new DbUpdateException(
                "unique collision",
                new SqliteException(
                    "UNIQUE constraint failed: DeviceTokens.UserId, DeviceTokens.InstallationId",
                    errorCode: 19,
                    extendedErrorCode: 2067)));
        DbContextOptions<AppDbContext> racingOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using AppDbContext racingContext = new(racingOptions);
        var repository = new EfDeviceTokenRepository(racingContext);

        DeviceToken result = await repository.UpsertAsync(
            userId,
            "installation-race",
            new string('b', 64),
            "ios",
            "production",
            "com.example.app");

        result.Token.Should().Be(new string('b', 64));
        await using AppDbContext verify = new(plainOptions);
        (await verify.DeviceTokens.CountAsync()).Should().Be(1);
        (await verify.DeviceTokens.SingleAsync()).Token.Should().Be(new string('b', 64));
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
    [InlineData("UNIQUE constraint failed: DeviceTokens.UserId, DeviceTokens.InstallationId", true)]
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
    [InlineData("IX_DeviceTokens_UserId_InstallationId", true)]
    [InlineData("ix_devicetokens_userid_installationid", false)]
    [InlineData("IX_DeviceTokens_UserId_InstallationId_Extra", false)]
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

    private sealed class CollisionInterceptor(
        DbContextOptions<AppDbContext> plainOptions,
        Guid userId,
        DbUpdateException exception) : SaveChangesInterceptor
    {
        private int _invocationCount;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _invocationCount) == 1)
            {
                await using var winner = new AppDbContext(plainOptions);
                winner.DeviceTokens.Add(new DeviceToken
                {
                    UserId = userId,
                    InstallationId = "installation-race",
                    Token = new string('a', 64),
                    Platform = "ios",
                    Environment = "production",
                    AppBundleId = "com.example.app",
                });
                await winner.SaveChangesAsync(cancellationToken);
                throw exception;
            }

            return result;
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
