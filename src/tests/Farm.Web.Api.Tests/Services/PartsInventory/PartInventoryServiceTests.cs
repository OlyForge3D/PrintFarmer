using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.PartsInventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PartsInventory;

/// <summary>
/// Unit tests for <see cref="PartInventoryService"/>. Uses an EF Core
/// in-memory provider so tests exercise the same DbContext code paths as
/// production (aggregate update + ledger insert), with idempotency behavior
/// asserted at the service layer.
/// </summary>
public class PartInventoryServiceTests : IDisposable
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PartInventoryServiceTests()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var factoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        _ = factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_options));
        _factory = factoryMock.Object;
    }

    public void Dispose()
    {
        using var db = new AppDbContext(_options);
        _ = db.Database.EnsureDeleted();
        GC.SuppressFinalize(this);
    }

    private PartInventoryService CreateSut() => new(_factory, NullLogger<PartInventoryService>.Instance);

    private async Task<PartInventory> SeedSkuAsync(string sku = "PF-TEST-01", int onHand = 0, int reorder = 0)
    {
        await using var db = new AppDbContext(_options);
        var part = new PartInventory
        {
            Id = Guid.NewGuid(),
            Sku = sku,
            Name = sku,
            OnHand = onHand,
            ReorderPoint = reorder,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = db.PartInventories.Add(part);
        _ = await db.SaveChangesAsync();
        return part;
    }

    private async Task<Bin> SeedBinAsync(string code = "BIN-A")
    {
        await using var db = new AppDbContext(_options);
        var bin = new Bin
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = code,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = db.Bins.Add(bin);
        _ = await db.SaveChangesAsync();
        return bin;
    }

    [Fact]
    public async Task AdjustAsync_AppendsLedgerEntryAndUpdatesOnHand()
    {
        _ = await SeedSkuAsync(onHand: 4);
        PartInventoryService sut = CreateSut();

        AdjustResult result = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(3, PartAdjustmentReason.Manual, null, null, "topped up", null, "op1"));

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        Assert.Equal(7, result.NewOnHand);

        await using var db = new AppDbContext(_options);
        PartInventory refreshed = await db.PartInventories.SingleAsync();
        Assert.Equal(7, refreshed.OnHand);
        List<PartInventoryAdjustment> ledger = await db.PartInventoryAdjustments.ToListAsync();
        _ = Assert.Single(ledger);
        Assert.Equal(3, ledger[0].Delta);
        Assert.Equal(PartAdjustmentReason.Manual, ledger[0].Reason);
    }

    [Fact]
    public async Task AdjustAsync_ZeroDelta_ReturnsInvalidRequest()
    {
        _ = await SeedSkuAsync();
        PartInventoryService sut = CreateSut();

        AdjustResult result = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(0, PartAdjustmentReason.Manual, null, null, null, null, null));

        Assert.Equal(PartInventoryOutcome.InvalidRequest, result.Outcome);
        await using var db = new AppDbContext(_options);
        Assert.Empty(await db.PartInventoryAdjustments.ToListAsync());
    }

    [Fact]
    public async Task AdjustAsync_UnknownSku_ReturnsPartNotFound()
    {
        PartInventoryService sut = CreateSut();

        AdjustResult result = await sut.AdjustAsync(
            "MISSING",
            new AdjustCommand(1, PartAdjustmentReason.Manual, null, null, null, null, null));

        Assert.Equal(PartInventoryOutcome.PartNotFound, result.Outcome);
    }

    [Fact]
    public async Task AdjustAsync_UnknownBinCode_ReturnsBinNotFound_AndDoesNotWrite()
    {
        _ = await SeedSkuAsync(onHand: 2);
        PartInventoryService sut = CreateSut();

        AdjustResult result = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(1, PartAdjustmentReason.Harvest, null, "NOPE", null, null, null));

        Assert.Equal(PartInventoryOutcome.BinNotFound, result.Outcome);
        await using var db = new AppDbContext(_options);
        Assert.Empty(await db.PartInventoryAdjustments.ToListAsync());
        Assert.Equal(2, (await db.PartInventories.SingleAsync()).OnHand);
    }

    [Fact]
    public async Task AdjustAsync_DuplicateOperationKey_ReturnsIdempotentReplay_NoNewLedgerEntry()
    {
        _ = await SeedSkuAsync(onHand: 0);
        PartInventoryService sut = CreateSut();

        AdjustResult first = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(5, PartAdjustmentReason.Manual, null, null, null, "op-dup", "u1"));
        AdjustResult second = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(5, PartAdjustmentReason.Manual, null, null, null, "op-dup", "u1"));

        Assert.Equal(PartInventoryOutcome.Ok, first.Outcome);
        Assert.Equal(PartInventoryOutcome.IdempotentReplay, second.Outcome);
        Assert.Equal(5, second.NewOnHand);

        await using var db = new AppDbContext(_options);
        Assert.Single(await db.PartInventoryAdjustments.ToListAsync());
        Assert.Equal(5, (await db.PartInventories.SingleAsync()).OnHand);
    }

    [Fact]
    public async Task AdjustAsync_UsesBinCode_WhenProvided()
    {
        _ = await SeedSkuAsync(onHand: 1);
        Bin bin = await SeedBinAsync("BIN-Q");
        PartInventoryService sut = CreateSut();

        AdjustResult result = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(2, PartAdjustmentReason.Harvest, null, "BIN-Q", null, null, null));

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Adjustment);
        Assert.Equal(bin.Id, result.Adjustment!.BinId);
        Assert.Equal("BIN-Q", result.Adjustment.BinCode);
    }
}
