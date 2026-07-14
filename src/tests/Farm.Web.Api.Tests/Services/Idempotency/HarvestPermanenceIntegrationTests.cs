using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Services.Idempotency;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Idempotency;

/// <summary>
/// Integration coverage for Vasquez blocker #2: the HTTP <c>Idempotency-Key</c>
/// replay window (7 days) is a convenience layer, not the durable guarantee. Even
/// after an idempotency record expires and is pruned, the natural domain
/// idempotency on harvest — the atomic <see cref="PrintJob.HarvestedAt"/> claim
/// plus the <see cref="PartHarvestOutputSnapshot"/> uniqueness — must still prevent
/// a job from being harvested twice.
///
/// <para>
/// Both subsystems run over the same SQLite-in-memory database so the test proves
/// they are genuinely independent: pruning every idempotency record does not
/// weaken the permanent, domain-level double-harvest guard.
/// </para>
/// </summary>
public class HarvestPermanenceIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public HarvestPermanenceIntegrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using AppDbContext db = new(_options);
        _ = db.Database.EnsureCreated();

        Mock<IDbContextFactory<AppDbContext>> factoryMock = new();
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

    private async Task<(Guid JobId, Guid PartId)> SeedCompletedJobWithMappingAsync(int mappingQuantity)
    {
        await using AppDbContext db = new(_options);

        FolderNode folder = new()
        {
            Id = Guid.NewGuid(),
            Path = "/tests",
            FolderType = "gcode",
        };
        _ = db.Set<FolderNode>().Add(folder);

        GcodeFile gcode = new()
        {
            Id = Guid.NewGuid(),
            Name = "part.gcode",
            FileName = "part.gcode",
            FolderId = folder.Id,
            FilePath = "/tests",
            FileHash = "hash",
            FileSizeBytes = 1,
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = db.Set<GcodeFile>().Add(gcode);

        Bin defaultBin = new()
        {
            Id = Guid.NewGuid(),
            Code = ($"BIN-{Guid.NewGuid():N}"[..16]).ToUpperInvariant(),
            Name = "Default",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = db.Bins.Add(defaultBin);

        PartInventory part = new()
        {
            Id = Guid.NewGuid(),
            Sku = "PF-BRKT-01",
            Name = "Bracket",
            DefaultBinId = defaultBin.Id,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = db.PartInventories.Add(part);

        PartOutputMapping mapping = new()
        {
            Id = Guid.NewGuid(),
            PartInventoryId = part.Id,
            GcodeFileId = gcode.Id,
            Quantity = mappingQuantity,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = db.PartOutputMappings.Add(mapping);

        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "part.gcode",
            Status = PrintJobStatus.Completed,
            Copies = 1,
            GcodeFileId = gcode.Id,
        };
        _ = db.PrintJobs.Add(job);

        _ = await db.SaveChangesAsync();
        return (job.Id, part.Id);
    }

    [Fact]
    public async Task DoubleHarvest_IsBlockedByDomainIdempotency_EvenAfterIdempotencyRecordPruned()
    {
        (Guid jobId, Guid partId) = await SeedCompletedJobWithMappingAsync(mappingQuantity: 2);

        PartHarvestService harvest = new(_factory, NullLogger<PartHarvestService>.Instance);
        IdempotencyStore idempotency = new(_factory, NullLogger<IdempotencyStore>.Instance);

        // ---- First harvest succeeds and applies the adjustment + snapshot. ----
        HarvestResult first = await harvest.HarvestJobAsync(jobId, new HarvestJobRequest(), "operator-1");
        _ = first.Outcome.Should().Be(PartInventoryOutcome.Ok);
        _ = first.Response!.AlreadyHarvested.Should().BeFalse();

        // ---- Model the HTTP idempotency-key layer for that first harvest, then let
        // its 7-day window lapse: seed an aged record and prune it. ----
        string effectiveRouteKey = $"{IdempotencyRouteKeys.JobQueueHarvest}|/api/job-queue/{jobId:N}/harvest";
        await using (AppDbContext seed = new(_options))
        {
            _ = seed.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                UserId = "operator-1",
                RouteKey = effectiveRouteKey,
                IdempotencyKey = "harvest-key-1",
                RequestHash = "hash",
                Status = IdempotencyRecordStatus.Completed,
                ResponseStatusCode = 200,
                CreatedAt = DateTime.UtcNow - TimeSpan.FromDays(8),
                UpdatedAt = DateTime.UtcNow - TimeSpan.FromDays(8),
            });
            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        int pruned = await idempotency.PruneExpiredAsync(DateTime.UtcNow, CancellationToken.None);
        _ = pruned.Should().Be(1, "the aged idempotency record is past the retention window");

        await using (AppDbContext afterPrune = new(_options))
        {
            _ = (await afterPrune.IdempotencyRecords.CountAsync(CancellationToken.None))
                .Should().Be(0, "the idempotency-key window has fully lapsed");
        }

        // ---- Second harvest, now with NO idempotency record backing it, must still
        // be blocked by the permanent domain guard, not double-apply. ----
        HarvestResult second = await harvest.HarvestJobAsync(jobId, new HarvestJobRequest(), "operator-1");
        _ = second.Outcome.Should().Be(PartInventoryOutcome.IdempotentReplay,
            "HarvestedAt + PartHarvestOutputSnapshot uniqueness survive idempotency-window expiry");
        _ = second.Response!.AlreadyHarvested.Should().BeTrue();

        await using AppDbContext verify = new(_options);
        _ = (await verify.PartInventories.SingleAsync(p => p.Id == partId)).OnHand
            .Should().Be(2, "on-hand must reflect exactly one harvest of quantity 2");
        _ = (await verify.PartInventoryAdjustments.CountAsync(CancellationToken.None))
            .Should().Be(1, "exactly one harvest adjustment may ever be written for the job");
        _ = (await verify.PartHarvestOutputSnapshots.CountAsync(CancellationToken.None))
            .Should().Be(1, "the permanent harvest snapshot is the durable double-harvest guard");
    }
}
