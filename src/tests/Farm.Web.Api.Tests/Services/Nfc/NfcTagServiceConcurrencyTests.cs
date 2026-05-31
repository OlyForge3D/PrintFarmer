using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.NfcDevices;
using Farm.Infrastructure.Services.SignalR;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Nfc;

/// <summary>
/// Verifies that concurrent POST /api/nfc/link calls with the same TagUid
/// produce exactly one binding (idempotent upsert) without throwing 500s.
/// </summary>
public class NfcTagServiceConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Mock<IHubContext<NfcHub>> _hubMock;

    public NfcTagServiceConcurrencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();

        var clientProxyMock = new Mock<IClientProxy>();
        var hubClientsMock = new Mock<IHubClients>();
        hubClientsMock.Setup(c => c.All).Returns(clientProxyMock.Object);
        _hubMock = new Mock<IHubContext<NfcHub>>();
        _hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private NfcTagService CreateService(AppDbContext db)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => db);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new NfcTagService(scopeFactory, _hubMock.Object, NullLogger<NfcTagService>.Instance);
    }

    [Fact]
    public async Task LinkTagAsync_ConcurrentCalls_SameTagUid_ProducesExactlyOneBinding()
    {
        const string tagUid = "AA:BB:CC:DD";
        const int parallelism = 10;

        var tasks = Enumerable.Range(0, parallelism).Select(async i =>
        {
            // Each parallel call gets its own DbContext (simulates separate HTTP requests)
            await using var db = new AppDbContext(_options);
            var service = CreateService(db);

            var request = new LinkNfcTagRequest
            {
                TagUid = tagUid,
                SpoolId = 42,
                SpoolName = $"PLA Black #{i}",
                PrinterId = null,
                TrayId = "A1"
            };

            return await service.LinkTagAsync(request, CancellationToken.None);
        });

        var results = await Task.WhenAll(tasks);

        // All callers should receive a 200 (no exceptions thrown)
        results.Should().HaveCount(parallelism);
        results.Should().AllSatisfy(r =>
        {
            r.TagUid.Should().Be(tagUid);
            r.SpoolId.Should().Be(42);
        });

        // Only one binding should exist in the database
        await using var verifyDb = new AppDbContext(_options);
        var bindings = await verifyDb.NfcTagBindings
            .Where(b => b.TagUid == tagUid)
            .ToListAsync();

        bindings.Should().HaveCount(1, "exactly one binding should survive the race");
    }

    [Fact]
    public async Task LinkTagAsync_ConcurrentCalls_DifferentTagUids_AllSucceed()
    {
        const int parallelism = 10;

        var tasks = Enumerable.Range(0, parallelism).Select(async i =>
        {
            await using var db = new AppDbContext(_options);
            var service = CreateService(db);

            var request = new LinkNfcTagRequest
            {
                TagUid = $"TAG:{i:D4}",
                SpoolId = i,
                SpoolName = $"Spool #{i}"
            };

            return await service.LinkTagAsync(request, CancellationToken.None);
        });

        var results = await Task.WhenAll(tasks);
        results.Should().HaveCount(parallelism);

        await using var verifyDb = new AppDbContext(_options);
        var count = await verifyDb.NfcTagBindings.CountAsync();
        count.Should().Be(parallelism);
    }

    [Fact]
    public async Task LinkTagAsync_UpdateExistingBinding_IsIdempotent()
    {
        const string tagUid = "UPDATE:TEST";

        // Create initial binding
        await using (var db = new AppDbContext(_options))
        {
            var service = CreateService(db);
            await service.LinkTagAsync(new LinkNfcTagRequest
            {
                TagUid = tagUid,
                SpoolId = 1,
                SpoolName = "Original"
            }, CancellationToken.None);
        }

        // Update it concurrently
        var tasks = Enumerable.Range(0, 5).Select(async i =>
        {
            await using var db = new AppDbContext(_options);
            var service = CreateService(db);

            return await service.LinkTagAsync(new LinkNfcTagRequest
            {
                TagUid = tagUid,
                SpoolId = 99,
                SpoolName = $"Updated #{i}"
            }, CancellationToken.None);
        });

        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(r => r.SpoolId.Should().Be(99));

        await using var verifyDb = new AppDbContext(_options);
        var bindings = await verifyDb.NfcTagBindings
            .Where(b => b.TagUid == tagUid)
            .ToListAsync();
        bindings.Should().HaveCount(1);
    }
}
