using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PartsInventory;

public class BarcodePartsInventoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public BarcodePartsInventoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = new AppDbContext(_options);
        _ = db.Database.EnsureCreated();
        var factory = new Mock<IDbContextFactory<AppDbContext>>();
        factory.Setup(value => value.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_options));
        _factory = factory.Object;
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SaveChangesAsync_DuplicateBinBarcode_ThrowsUniqueViolation()
    {
        await using var db = new AppDbContext(_options);
        db.Bins.AddRange(
            new Bin { Id = Guid.NewGuid(), Code = "BIN-UNIQUE", Name = "One", IsActive = true },
            new Bin { Id = Guid.NewGuid(), Code = "BIN-UNIQUE", Name = "Two", IsActive = true });

        _ = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task LogAsync_PartAndBinReferences_RoundTripWithExistingFields()
    {
        Guid binId = Guid.NewGuid();
        Guid partId = Guid.NewGuid();
        var settings = new Mock<ISettingsService>();
        settings.Setup(value => value.Get<SpoolmanSettings>())
            .Returns(new SpoolmanSettings { BarcodeScanDebugLoggingEnabled = true });
        var service = new BarcodeScanLogService(
            _factory,
            settings.Object,
            NullLogger<BarcodeScanLogService>.Instance);

        await service.LogAsync(new BarcodeScanLog
        {
            Barcode = "BIN-1",
            Action = BarcodeScanAction.BinScan,
            Outcome = BarcodeScanOutcome.Resolved,
            HttpStatus = 200,
            MatchedFilamentId = 12,
            CreatedSpoolId = 34,
            BinId = binId,
            PartInventoryId = partId,
            UserId = "actor",
        });

        BarcodeScanLog log = Assert.Single(await service.GetRecentAsync(10, CancellationToken.None));
        Assert.Equal(12, log.MatchedFilamentId);
        Assert.Equal(34, log.CreatedSpoolId);
        Assert.Equal(binId, log.BinId);
        Assert.Equal(partId, log.PartInventoryId);
        Assert.Equal("actor", log.UserId);
    }
}
