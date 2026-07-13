using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Idempotency;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Idempotency;

/// <summary>
/// Verifies the periodic cleanup hosted service delegates to the store's
/// prune path under a fresh service scope and swallows transient failures
/// so the host is not torn down when the DB is temporarily unavailable.
/// </summary>
public class IdempotencyRecordCleanupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IdempotencyStore _store;

    public IdempotencyRecordCleanupServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using AppDbContext db = new(_options);
        _ = db.Database.EnsureCreated();

        Mock<IDbContextFactory<AppDbContext>> factoryMock = new();
        _ = factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_options));
        _store = new IdempotencyStore(factoryMock.Object, NullLogger<IdempotencyStore>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private ServiceProvider BuildRootProvider()
    {
        ServiceCollection services = new();
        _ = services.AddScoped<IIdempotencyStore>(_ => _store);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task RunOnce_PrunesExpiredRecords()
    {
        DateTime now = DateTime.UtcNow;
        using (AppDbContext seed = new(_options))
        {
            _ = seed.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                UserId = "u",
                RouteKey = "r",
                IdempotencyKey = "k",
                RequestHash = "h",
                Status = IdempotencyRecordStatus.Completed,
                CreatedAt = now - TimeSpan.FromDays(30),
                UpdatedAt = now - TimeSpan.FromDays(30),
            });
            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        using ServiceProvider sp = BuildRootProvider();
        IdempotencyRecordCleanupService svc = new(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<IdempotencyRecordCleanupService>.Instance,
            TimeSpan.FromMinutes(1));

        await svc.RunOnceAsync(CancellationToken.None);

        using AppDbContext verify = new(_options);
        _ = (await verify.IdempotencyRecords.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task RunOnce_SwallowsExceptions()
    {
        // Build a provider where the store throws — the service must not propagate.
        Mock<IIdempotencyStore> throwing = new();
        _ = throwing.Setup(s => s.PruneExpiredAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient"));

        ServiceCollection services = new();
        _ = services.AddScoped(_ => throwing.Object);
        using ServiceProvider sp = services.BuildServiceProvider();

        IdempotencyRecordCleanupService svc = new(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<IdempotencyRecordCleanupService>.Instance,
            TimeSpan.FromMinutes(1));

        Func<Task> act = () => svc.RunOnceAsync(CancellationToken.None);
        _ = await act.Should().NotThrowAsync("the cleanup loop must tolerate transient store failures");
    }
}
