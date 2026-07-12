using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Web.Api.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PartsInventory;

/// <summary>
/// Relational-provider tests for <see cref="PartInventoryService"/>.
/// <para>
/// Uses SQLite in-memory (with a persistent shared connection) so that the
/// composite unique index <c>(PartInventoryId, OperationKey)</c>, real
/// transactions, and PRAGMA foreign keys are all exercised. The prior
/// InMemory-provider variant of these tests could not reproduce the
/// idempotency / atomic-create bugs surfaced by the #714 convergence review.
/// </para>
/// </summary>
public class PartInventoryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PartInventoryServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new AppDbContext(_options);
        _ = db.Database.EnsureCreated();

        var factoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        _ = factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_options));
        _factory = factoryMock.Object;
    }

    public void Dispose()
    {
        _connection.Dispose();
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
            new AdjustCommand(3, PartAdjustmentReason.Manual, null, null, "topped up", "op1", null));

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
    public async Task AdjustAsync_DuplicateOperationKey_ReturnsIdempotentReplay_WithCommittedOnHand()
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

        // The prior bug returned the mutated in-memory OnHand from a rolled-back
        // transaction (10). Correct behaviour: return the committed value (5).
        Assert.Equal(5, second.NewOnHand);
        Assert.NotNull(second.Adjustment);
        Assert.Equal("op-dup", second.Adjustment!.OperationKey);

        await using var db = new AppDbContext(_options);
        _ = Assert.Single(await db.PartInventoryAdjustments.ToListAsync());
        Assert.Equal(5, (await db.PartInventories.SingleAsync()).OnHand);
    }

    [Fact]
    public async Task AdjustAsync_DifferentSkus_ShareOperationKey()
    {
        // Composite unique constraint must be (PartInventoryId, OperationKey),
        // not OperationKey alone. Two independent SKUs performing their own
        // retries with the same client-generated key must both succeed.
        _ = await SeedSkuAsync("PF-A", onHand: 0);
        _ = await SeedSkuAsync("PF-B", onHand: 0);
        PartInventoryService sut = CreateSut();

        AdjustResult a = await sut.AdjustAsync(
            "PF-A",
            new AdjustCommand(2, PartAdjustmentReason.Manual, null, null, null, "shared-key", null));
        AdjustResult b = await sut.AdjustAsync(
            "PF-B",
            new AdjustCommand(3, PartAdjustmentReason.Manual, null, null, null, "shared-key", null));

        Assert.Equal(PartInventoryOutcome.Ok, a.Outcome);
        Assert.Equal(PartInventoryOutcome.Ok, b.Outcome);

        await using var db = new AppDbContext(_options);
        Assert.Equal(2, (await db.PartInventories.SingleAsync(p => p.Sku == "PF-A")).OnHand);
        Assert.Equal(3, (await db.PartInventories.SingleAsync(p => p.Sku == "PF-B")).OnHand);
    }

    [Fact]
    public async Task AdjustAsync_ConcurrentSameOperationKey_CommitsExactlyOneRow()
    {
        _ = await SeedSkuAsync(onHand: 0);
        PartInventoryService sut = CreateSut();
        const int callers = 10;

        AdjustResult[] results = await Task.WhenAll(Enumerable.Range(0, callers)
            .Select(_ => sut.AdjustAsync(
                "PF-TEST-01",
                new AdjustCommand(7, PartAdjustmentReason.Manual, null, null, null, "race-key", "u1"))));

        // Every caller returns success or idempotent replay — never a 500 /
        // Conflict / stale value from a poisoned transaction.
        Assert.All(results, r => Assert.True(
            r.Outcome == PartInventoryOutcome.Ok || r.Outcome == PartInventoryOutcome.IdempotentReplay,
            $"Unexpected outcome {r.Outcome}: {r.Message}"));

        int oks = results.Count(r => r.Outcome == PartInventoryOutcome.Ok);
        Assert.Equal(1, oks);

        await using var db = new AppDbContext(_options);
        _ = Assert.Single(await db.PartInventoryAdjustments.ToListAsync());
        Assert.Equal(7, (await db.PartInventories.SingleAsync()).OnHand);

        // Every replay must expose the committed OnHand, never a locally-computed value.
        Assert.All(results, r => Assert.Equal(7, r.NewOnHand));
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

    [Fact]
    public async Task CreatePartAsync_WithInitialOnHand_CommitsPartAndLedgerAtomically()
    {
        _ = await SeedBinAsync("BIN-C");
        PartInventoryService sut = CreateSut();

        CreatePartResult result = await sut.CreatePartAsync(new CreatePartCommand(
            Sku: "PF-NEW-01",
            Name: "New Bracket",
            Description: null,
            ModelFileRef: null,
            DefaultBinCode: "BIN-C",
            InitialOnHand: 5,
            ReorderPoint: 2,
            UserId: "creator"));

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Part);
        Assert.Equal(5, result.Part!.OnHand);

        await using var db = new AppDbContext(_options);
        PartInventory refreshed = await db.PartInventories.SingleAsync(p => p.Sku == "PF-NEW-01");
        Assert.Equal(5, refreshed.OnHand);

        PartInventoryAdjustment ledger = await db.PartInventoryAdjustments
            .SingleAsync(a => a.PartInventoryId == refreshed.Id);
        Assert.Equal(5, ledger.Delta);
        Assert.Equal(PartAdjustmentReason.Manual, ledger.Reason);
        Assert.Null(ledger.OperationKey);
        Assert.Equal("creator", ledger.UserId);
    }

    [Fact]
    public async Task CreatePartAsync_ZeroInitialOnHand_CommitsSkuOnly()
    {
        PartInventoryService sut = CreateSut();

        CreatePartResult result = await sut.CreatePartAsync(new CreatePartCommand(
            Sku: "PF-EMPTY-01",
            Name: "Empty",
            Description: null,
            ModelFileRef: null,
            DefaultBinCode: null,
            InitialOnHand: 0,
            ReorderPoint: 0,
            UserId: null));

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        await using var db = new AppDbContext(_options);
        _ = Assert.Single(await db.PartInventories.ToListAsync());
        Assert.Empty(await db.PartInventoryAdjustments.ToListAsync());
    }

    [Fact]
    public async Task CreatePartAsync_UnknownDefaultBin_ReturnsBinNotFound_AndCommitsNothing()
    {
        PartInventoryService sut = CreateSut();

        CreatePartResult result = await sut.CreatePartAsync(new CreatePartCommand(
            Sku: "PF-NOBIN-01",
            Name: "No Bin",
            Description: null,
            ModelFileRef: null,
            DefaultBinCode: "GHOST",
            InitialOnHand: 3,
            ReorderPoint: 0,
            UserId: null));

        Assert.Equal(PartInventoryOutcome.BinNotFound, result.Outcome);
        await using var db = new AppDbContext(_options);
        Assert.Empty(await db.PartInventories.ToListAsync());
        Assert.Empty(await db.PartInventoryAdjustments.ToListAsync());
    }

    [Fact]
    public async Task CreatePartAsync_DuplicateSku_ReturnsSkuAlreadyExists()
    {
        _ = await SeedSkuAsync("PF-DUP-01", onHand: 1);
        PartInventoryService sut = CreateSut();

        CreatePartResult result = await sut.CreatePartAsync(new CreatePartCommand(
            Sku: "PF-DUP-01",
            Name: "Dup",
            Description: null,
            ModelFileRef: null,
            DefaultBinCode: null,
            InitialOnHand: 0,
            ReorderPoint: 0,
            UserId: null));

        Assert.Equal(PartInventoryOutcome.SkuAlreadyExists, result.Outcome);
        await using var db = new AppDbContext(_options);
        // Only the seeded row exists.
        _ = Assert.Single(await db.PartInventories.ToListAsync());
    }

    [Fact]
    public async Task AdjustAsync_WouldMakeStockNegative_ReturnsInvalidAndPreservesLedger()
    {
        _ = await SeedSkuAsync(onHand: 2);

        AdjustResult result = await CreateSut().AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(-3, PartAdjustmentReason.Manual, null, null, null, null, "actor"));

        Assert.Equal(PartInventoryOutcome.InvalidRequest, result.Outcome);
        await using var db = new AppDbContext(_options);
        Assert.Equal(2, (await db.PartInventories.SingleAsync()).OnHand);
        Assert.Empty(await db.PartInventoryAdjustments.ToListAsync());
    }

    [Fact]
    public async Task AdjustAsync_WouldOverflowStock_ReturnsInvalidAndPreservesLedger()
    {
        _ = await SeedSkuAsync(onHand: int.MaxValue);

        AdjustResult result = await CreateSut().AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(1, PartAdjustmentReason.Manual, null, null, null, null, "actor"));

        Assert.Equal(PartInventoryOutcome.InvalidRequest, result.Outcome);
        await using var db = new AppDbContext(_options);
        Assert.Equal(int.MaxValue, (await db.PartInventories.SingleAsync()).OnHand);
        Assert.Empty(await db.PartInventoryAdjustments.ToListAsync());
    }

    [Fact]
    public async Task AdjustAsync_QcRejectPositiveDelta_ReturnsInvalid()
    {
        _ = await SeedSkuAsync(onHand: 2);

        AdjustResult result = await CreateSut().AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(1, PartAdjustmentReason.QcReject, null, null, null, null, null));

        Assert.Equal(PartInventoryOutcome.InvalidRequest, result.Outcome);
    }

    [Fact]
    public async Task CreatePartAsync_NormalizesSkuAndBinCode()
    {
        _ = await SeedBinAsync("BIN-N");

        CreatePartResult result = await CreateSut().CreatePartAsync(new CreatePartCommand(
            Sku: "  pf-normalized  ",
            Name: "Normalized",
            Description: null,
            ModelFileRef: null,
            DefaultBinCode: " bin-n ",
            InitialOnHand: 0,
            ReorderPoint: 1,
            UserId: null));

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        Assert.Equal("PF-NORMALIZED", result.Part!.Sku);
        Assert.Equal("BIN-N", result.Part.DefaultBin!.Code);
    }

    [Fact]
    public async Task AdjustAsync_FeatureDisabled_DoesNotOpenDatabase()
    {
        var factory = new Mock<IDbContextFactory<AppDbContext>>(MockBehavior.Strict);
        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Strict);
        gate.Setup(value => value.IsEnabled(OperatorFeature.PrintedPartsInventory)).Returns(false);
        var sut = new PartInventoryService(factory.Object, NullLogger<PartInventoryService>.Instance, gate.Object);

        AdjustResult result = await sut.AdjustAsync(
            "PF-TEST",
            new AdjustCommand(1, PartAdjustmentReason.Manual, null, null, null, null, null));

        Assert.Equal(PartInventoryOutcome.FeatureDisabled, result.Outcome);
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetReorderCandidatesAsync_OnHandEqualsThreshold_IncludesSku()
    {
        _ = await SeedSkuAsync(onHand: 5, reorder: 5);
        var sut = new ReorderEvaluationService(_factory);

        IReadOnlyList<Farm.Infrastructure.Dtos.PartsInventory.ReorderCandidateResponse> candidates =
            await sut.GetReorderCandidatesAsync();

        Farm.Infrastructure.Dtos.PartsInventory.ReorderCandidateResponse candidate = Assert.Single(candidates);
        Assert.Equal("PF-TEST-01", candidate.Sku);
        Assert.Equal(0, candidate.Deficit);
    }
}
