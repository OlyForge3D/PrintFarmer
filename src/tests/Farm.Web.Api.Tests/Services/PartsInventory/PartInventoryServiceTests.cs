using System.Text.Json;
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

    [Theory]
    [InlineData(PartAdjustmentReason.Harvest, "\"harvest\"")]
    [InlineData(PartAdjustmentReason.QcReject, "\"qc-reject\"")]
    [InlineData(PartAdjustmentReason.Manual, "\"manual\"")]
    public void PartAdjustmentReason_SerializesWithExactWireValue(
        PartAdjustmentReason reason,
        string expectedJson)
    {
        Assert.Equal(expectedJson, JsonSerializer.Serialize(reason));
        Assert.Equal(reason, JsonSerializer.Deserialize<PartAdjustmentReason>(expectedJson));
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
        Assert.Equal(7, ledger[0].ResultingBalance);
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
    public async Task AdjustAsync_ClientOperationKeyWithReservedPrefix_ReturnsInvalidRequest_AndDoesNotWrite()
    {
        // Hicks r3 blocker 2: the "idem:" namespace is reserved for the server-synthesized
        // backstop key. A client-supplied operationKey in that namespace must be rejected before
        // any mutation, so a crafted value can never collide with a future synthesized key and
        // silently dedup a genuinely distinct adjustment.
        _ = await SeedSkuAsync(onHand: 4);
        PartInventoryService sut = CreateSut();

        AdjustResult result = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(3, PartAdjustmentReason.Manual, null, null, null, "idem:foo", "u1"));

        Assert.Equal(PartInventoryOutcome.InvalidRequest, result.Outcome);
        string? message = result.Message;
        Assert.NotNull(message);
        Assert.Contains("idem:", message, StringComparison.OrdinalIgnoreCase);

        await using var db = new AppDbContext(_options);
        Assert.Empty(await db.PartInventoryAdjustments.ToListAsync());
        Assert.Equal(4, (await db.PartInventories.SingleAsync()).OnHand);
    }

    [Fact]
    public async Task AdjustAsync_ClientOperationKeyWithReservedPrefix_IsCaseInsensitive()
    {
        // The reserved-prefix guard must be case-insensitive: "Idem:Foo" is just as dangerous a
        // collision vector as "idem:foo".
        _ = await SeedSkuAsync(onHand: 4);
        PartInventoryService sut = CreateSut();

        AdjustResult result = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(3, PartAdjustmentReason.Manual, null, null, null, "Idem:Foo", "u1"));

        Assert.Equal(PartInventoryOutcome.InvalidRequest, result.Outcome);
        await using var db = new AppDbContext(_options);
        Assert.Empty(await db.PartInventoryAdjustments.ToListAsync());
    }

    [Fact]
    public async Task AdjustAsync_ClientOperationKeyWithPrefixNotAtStart_IsAllowed()
    {
        // Only the reserved prefix AT THE START is rejected. "myapp:idem:foo" is a legitimate
        // namespaced client key and must be honored normally.
        _ = await SeedSkuAsync(onHand: 4);
        PartInventoryService sut = CreateSut();

        AdjustResult result = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(3, PartAdjustmentReason.Manual, null, null, null, "myapp:idem:foo", "u1"));

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        Assert.Equal(7, result.NewOnHand);

        await using var db = new AppDbContext(_options);
        PartInventoryAdjustment ledger = Assert.Single(await db.PartInventoryAdjustments.ToListAsync());
        Assert.Equal("myapp:idem:foo", ledger.OperationKey);
    }

    [Fact]
    public async Task AdjustAsync_ClientOperationKeyWithFullwidthReservedPrefix_ReturnsInvalidRequest_AndDoesNotWrite()
    {
        // Hicks r4 blocker 2: the service-layer defense-in-depth guard must be width-aware. A
        // fullwidth "ｉｄｅｍ:" (U+FF49 U+FF44 U+FF45 U+FF4D) folds to ASCII "idem:" under SQL Server's
        // width-insensitive collation, so it must be rejected here exactly like ASCII "idem:" —
        // otherwise it slips past the ordinal guard, is stored, and can collide with a
        // server-synthesized key.
        _ = await SeedSkuAsync(onHand: 4);
        PartInventoryService sut = CreateSut();

        AdjustResult result = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(3, PartAdjustmentReason.Manual, null, null, null, "\uFF49\uFF44\uFF45\uFF4D:foo", "u1"));

        Assert.Equal(PartInventoryOutcome.InvalidRequest, result.Outcome);
        string? message = result.Message;
        Assert.NotNull(message);
        Assert.Contains("idem:", message, StringComparison.OrdinalIgnoreCase);

        await using var db = new AppDbContext(_options);
        Assert.Empty(await db.PartInventoryAdjustments.ToListAsync());
        Assert.Equal(4, (await db.PartInventories.SingleAsync()).OnHand);
    }

    [Fact]
    public async Task AdjustAsync_WidthEquivalentSku_ResolvesToSameSeededRow()
    {
        // Hicks r4 blocker 1 at the service layer: the domain lookup must apply the same NFKC
        // normalization as the idempotency route key, or a fullwidth SKU would be looked up as
        // fullwidth against SQL Server's width-insensitive collation and the double-apply path
        // would survive at a different layer than the filter. Seed ASCII "ABC" and adjust via
        // fullwidth "ＡＢＣ" (U+FF21..U+FF23): the shared PartInventoryIdentity.NormalizeSku fold
        // makes both spellings resolve to the one seeded row.
        _ = await SeedSkuAsync(sku: "ABC", onHand: 5);
        PartInventoryService sut = CreateSut();

        AdjustResult result = await sut.AdjustAsync(
            "\uFF21\uFF22\uFF23",
            new AdjustCommand(2, PartAdjustmentReason.Manual, null, null, null, null, "u1"));

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        Assert.Equal(7, result.NewOnHand);

        await using var db = new AppDbContext(_options);
        PartInventory part = await db.PartInventories.SingleAsync();
        Assert.Equal("ABC", part.Sku);
        Assert.Equal(7, part.OnHand);
        _ = Assert.Single(await db.PartInventoryAdjustments.ToListAsync());
    }

    [Fact]
    public async Task AdjustAsync_SynthesizedOperationKeyChannel_AppliesOnce_AndBacksIdempotency()
    {
        // Regression for Hudson's B2 backstop under the r3 channel-split: when the client omits
        // its operationKey, the trusted synthesized key arrives on the SynthesizedOperationKey
        // channel (which legitimately uses the reserved "idem:" prefix). It must NOT be rejected,
        // must apply the delta once, and must dedup a retry of the same synthesized key.
        _ = await SeedSkuAsync(onHand: 0);
        PartInventoryService sut = CreateSut();
        const string synthesized = "idem:deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef";

        AdjustResult first = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(5, PartAdjustmentReason.Manual, null, null, null, null, "u1", synthesized));
        AdjustResult second = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(5, PartAdjustmentReason.Manual, null, null, null, null, "u1", synthesized));

        Assert.Equal(PartInventoryOutcome.Ok, first.Outcome);
        Assert.Equal(5, first.NewOnHand);
        Assert.Equal(PartInventoryOutcome.IdempotentReplay, second.Outcome);
        Assert.Equal(5, second.NewOnHand);

        await using var db = new AppDbContext(_options);
        _ = Assert.Single(await db.PartInventoryAdjustments.ToListAsync());
        Assert.Equal(5, (await db.PartInventories.SingleAsync()).OnHand);
    }

    [Fact]
    public async Task AdjustAsync_ClientOperationKey_TakesPrecedenceOverSynthesized()
    {
        // When BOTH channels are present the client's key wins (the synthesized backstop is only a
        // fallback for the client-omitted case). The persisted ledger records the client key.
        _ = await SeedSkuAsync(onHand: 0);
        PartInventoryService sut = CreateSut();
        const string synthesized = "idem:deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef";

        AdjustResult result = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(5, PartAdjustmentReason.Manual, null, null, null, "client-key", "u1", synthesized));

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        await using var db = new AppDbContext(_options);
        PartInventoryAdjustment ledger = Assert.Single(await db.PartInventoryAdjustments.ToListAsync());
        Assert.Equal("client-key", ledger.OperationKey);
    }

    [Fact]
    public async Task AdjustAsync_ReplayAfterLaterAdjustment_ReturnsOriginalResultingBalance()
    {
        _ = await SeedSkuAsync(onHand: 0);
        PartInventoryService sut = CreateSut();

        _ = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(5, PartAdjustmentReason.Manual, null, null, null, "original", "u1"));
        _ = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(2, PartAdjustmentReason.Manual, null, null, null, "later", "u1"));
        AdjustResult replay = await sut.AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(5, PartAdjustmentReason.Manual, null, null, null, "original", "u1"));

        Assert.Equal(PartInventoryOutcome.IdempotentReplay, replay.Outcome);
        Assert.Equal(5, replay.NewOnHand);
        Assert.Equal(5, replay.Adjustment!.ResultingBalance);
        await using var db = new AppDbContext(_options);
        Assert.Equal(7, (await db.PartInventories.SingleAsync()).OnHand);
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
    public async Task AdjustAsync_ConcurrentDistinctOperations_ProduceExactBalanceAndLedger()
    {
        string connectionString =
            $"Data Source=parts-adjust-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=30";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(anchor);
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using (var setup = new AppDbContext(options))
        {
            _ = setup.Database.EnsureCreated();
            _ = setup.PartInventories.Add(new PartInventory
            {
                Id = Guid.NewGuid(),
                Sku = "SKU-CONCURRENT",
                Name = "Concurrent",
                IsActive = true,
            });
            _ = await setup.SaveChangesAsync();
        }

        var factory = new Mock<IDbContextFactory<AppDbContext>>();
        factory.Setup(value => value.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(options));
        var sut = new PartInventoryService(
            factory.Object,
            NullLogger<PartInventoryService>.Instance);
        const int Writers = 8;

        AdjustResult[] results = await Task.WhenAll(Enumerable.Range(1, Writers)
            .Select(index => sut.AdjustAsync(
                "SKU-CONCURRENT",
                new AdjustCommand(
                    1,
                    PartAdjustmentReason.Manual,
                    null,
                    null,
                    null,
                    $"writer-{index}",
                    $"actor-{index}"))));

        Assert.All(results, result => Assert.Equal(PartInventoryOutcome.Ok, result.Outcome));
        await using var verify = new AppDbContext(options);
        Assert.Equal(Writers, (await verify.PartInventories.SingleAsync()).OnHand);
        List<PartInventoryAdjustment> ledger = await verify.PartInventoryAdjustments
            .OrderBy(value => value.ResultingBalance)
            .ToListAsync();
        Assert.Equal(Writers, ledger.Count);
        Assert.Equal(Enumerable.Range(1, Writers), ledger.Select(value => value.ResultingBalance));
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
        Assert.Equal(5, ledger.ResultingBalance);
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
    public async Task AdjustAsync_UnknownPrintJob_ReturnsJobNotFoundWithoutMutation()
    {
        _ = await SeedSkuAsync(onHand: 2);

        AdjustResult result = await CreateSut().AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(
                1,
                PartAdjustmentReason.Manual,
                Guid.NewGuid(),
                null,
                null,
                null,
                "actor"));

        Assert.Equal(PartInventoryOutcome.JobNotFound, result.Outcome);
        await using var db = new AppDbContext(_options);
        Assert.Equal(2, (await db.PartInventories.SingleAsync()).OnHand);
        Assert.Empty(await db.PartInventoryAdjustments.ToListAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_ModifyingOrDeletingLedgerEntry_IsRejected()
    {
        _ = await SeedSkuAsync(onHand: 0);
        _ = await CreateSut().AdjustAsync(
            "PF-TEST-01",
            new AdjustCommand(1, PartAdjustmentReason.Manual, null, null, null, null, "actor"));

        await using var db = new AppDbContext(_options);
        PartInventoryAdjustment adjustment = await db.PartInventoryAdjustments.SingleAsync();
        adjustment.Notes = "tampered";
        InvalidOperationException updateError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync());
        Assert.Contains("immutable", updateError.Message, StringComparison.OrdinalIgnoreCase);

        db.Entry(adjustment).State = EntityState.Unchanged;
        _ = db.PartInventoryAdjustments.Remove(adjustment);
        InvalidOperationException deleteError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync());
        Assert.Contains("immutable", deleteError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveChangesAsync_ChangingSkuOrBinCodeIdentity_IsRejected()
    {
        PartInventory part = await SeedSkuAsync();
        Bin bin = await SeedBinAsync();

        await using (var db = new AppDbContext(_options))
        {
            PartInventory trackedPart = await db.PartInventories.SingleAsync(value => value.Id == part.Id);
            trackedPart.Sku = "PF-RENAMED";
            InvalidOperationException skuError = await Assert.ThrowsAsync<InvalidOperationException>(
                () => db.SaveChangesAsync());
            Assert.Contains("immutable", skuError.Message, StringComparison.OrdinalIgnoreCase);
        }

        await using (var db = new AppDbContext(_options))
        {
            Bin trackedBin = await db.Bins.SingleAsync(value => value.Id == bin.Id);
            trackedBin.Code = "BIN-RENAMED";
            InvalidOperationException binError = await Assert.ThrowsAsync<InvalidOperationException>(
                () => db.SaveChangesAsync());
            Assert.Contains("immutable", binError.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Model_PartInventoryAndBin_DoNotExposeInertRowVersionTokens()
    {
        using var db = new AppDbContext(_options);
        Assert.Null(db.Model.FindEntityType(typeof(PartInventory))!.FindProperty("RowVersion"));
        Assert.Null(db.Model.FindEntityType(typeof(Bin))!.FindProperty("RowVersion"));
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
