using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
}
