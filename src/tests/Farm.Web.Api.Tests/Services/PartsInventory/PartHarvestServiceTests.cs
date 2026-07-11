using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Web.Api.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PartsInventory;

/// <summary>
/// Relational-provider tests for <see cref="PartHarvestService"/>. Uses SQLite
/// in-memory (shared connection) so that the composite unique index on
/// <c>PartInventoryAdjustments (PartInventoryId, OperationKey)</c>, real
/// transactions, and PRAGMA foreign keys are all exercised — the prior
/// InMemory-provider variant could not reproduce the concurrent-harvest bugs
/// surfaced by the #714 review.
/// </summary>
public class PartHarvestServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PartHarvestServiceTests()
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

    private PartHarvestService CreateSut() => new(_factory, NullLogger<PartHarvestService>.Instance);

    private async Task<(PrintJob Job, PartInventory Part)> SeedCompletedJobWithMappingAsync(
        int copies = 1,
        int mappingQuantity = 1,
        bool useProjectFile = false)
    {
        await using var db = new AppDbContext(_options);
        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            Path = "/tests",
            FolderType = "gcode",
        };
        _ = db.Set<FolderNode>().Add(folder);

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "part.gcode",
            FileName = "part.gcode",
            FolderId = folder.Id,
            FilePath = "/tmp",
            FileHash = "hash",
            FileSizeBytes = 1,
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = db.Set<GcodeFile>().Add(gcode);

        PrintProject? project = null;
        PrintProjectFile? projectFile = null;
        if (useProjectFile)
        {
            project = new PrintProject
            {
                Id = Guid.NewGuid(),
                Name = "proj",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _ = db.Set<PrintProject>().Add(project);
            projectFile = new PrintProjectFile
            {
                Id = Guid.NewGuid(),
                PrintProjectId = project.Id,
                GcodeFileId = gcode.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _ = db.Set<PrintProjectFile>().Add(projectFile);
        }

        var part = new PartInventory
        {
            Id = Guid.NewGuid(),
            Sku = "PF-BRKT-01",
            Name = "Bracket",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = db.PartInventories.Add(part);

        var mapping = new PartOutputMapping
        {
            Id = Guid.NewGuid(),
            PartInventoryId = part.Id,
            GcodeFileId = useProjectFile ? null : gcode.Id,
            PrintProjectFileId = useProjectFile ? projectFile!.Id : null,
            Quantity = mappingQuantity,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = db.PartOutputMappings.Add(mapping);

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "part.gcode",
            Status = PrintJobStatus.Completed,
            Copies = copies,
            GcodeFileId = gcode.Id,
            ProjectFileId = useProjectFile ? projectFile!.Id : null,
        };
        _ = db.PrintJobs.Add(job);
        _ = await db.SaveChangesAsync();
        return (job, part);
    }

    [Fact]
    public async Task HarvestJobAsync_UnknownJob_ReturnsJobNotFound()
    {
        PartHarvestService sut = CreateSut();
        HarvestResult result = await sut.HarvestJobAsync(Guid.NewGuid(), new HarvestJobRequest(), null);
        Assert.Equal(PartInventoryOutcome.JobNotFound, result.Outcome);
    }

    [Fact]
    public async Task HarvestJobAsync_NonCompletedJob_ReturnsJobNotCompleted()
    {
        Guid jobId;
        await using (var db = new AppDbContext(_options))
        {
            var job = new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "x.gcode",
                Status = PrintJobStatus.Printing,
                Copies = 1,
            };
            _ = db.PrintJobs.Add(job);
            _ = await db.SaveChangesAsync();
            jobId = job.Id;
        }

        PartHarvestService sut = CreateSut();
        HarvestResult result = await sut.HarvestJobAsync(jobId, new HarvestJobRequest(), null);
        Assert.Equal(PartInventoryOutcome.JobNotCompleted, result.Outcome);
    }

    [Fact]
    public async Task HarvestJobAsync_WithGcodeMapping_IncrementsSkuAndStampsJob()
    {
        (PrintJob job, PartInventory part) = await SeedCompletedJobWithMappingAsync(copies: 2, mappingQuantity: 3);

        PartHarvestService sut = CreateSut();
        HarvestResult result = await sut.HarvestJobAsync(job.Id, new HarvestJobRequest(), "op-1");

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Response);
        Assert.False(result.Response!.AlreadyHarvested);
        _ = Assert.Single(result.Response.Adjustments);

        await using var db = new AppDbContext(_options);
        PartInventory refreshed = await db.PartInventories.SingleAsync(p => p.Id == part.Id);
        Assert.Equal(6, refreshed.OnHand);

        PrintJob refreshedJob = await db.PrintJobs.SingleAsync(j => j.Id == job.Id);
        Assert.NotNull(refreshedJob.HarvestedAt);
        Assert.Equal($"harvest:{job.Id:N}", refreshedJob.HarvestOperationKey);
        Assert.Equal("op-1", refreshedJob.HarvestedByUserId);

        List<PartInventoryAdjustment> ledger = await db.PartInventoryAdjustments.ToListAsync();
        _ = Assert.Single(ledger);
        Assert.Equal(6, ledger[0].Delta);
        Assert.Equal(PartAdjustmentReason.Harvest, ledger[0].Reason);
        Assert.Equal(job.Id, ledger[0].PrintJobId);
    }

    [Fact]
    public async Task HarvestJobAsync_ExplicitOutputs_TakePrecedenceOverMappings()
    {
        (PrintJob job, PartInventory bracket) = await SeedCompletedJobWithMappingAsync(copies: 2, mappingQuantity: 3);

        Guid altId;
        await using (var db = new AppDbContext(_options))
        {
            var alt = new PartInventory
            {
                Id = Guid.NewGuid(),
                Sku = "PF-CLIP-99",
                Name = "Clip",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _ = db.PartInventories.Add(alt);
            _ = await db.SaveChangesAsync();
            altId = alt.Id;
        }

        PartHarvestService sut = CreateSut();
        HarvestResult result = await sut.HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(Outputs: new[] { new HarvestOutputRequestItem("PF-CLIP-99", 4) }),
            null);

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        await using var db2 = new AppDbContext(_options);
        Assert.Equal(0, (await db2.PartInventories.SingleAsync(p => p.Id == bracket.Id)).OnHand);
        Assert.Equal(4, (await db2.PartInventories.SingleAsync(p => p.Id == altId)).OnHand);
    }

    [Fact]
    public async Task HarvestJobAsync_ProjectFileMapping_TakesPrecedenceOverGcodeMapping()
    {
        Guid projectSkuId;
        Guid gcodeSkuId;
        Guid jobId;

        await using (var db = new AppDbContext(_options))
        {
            var folder = new FolderNode { Id = Guid.NewGuid(), Path = "/tests", FolderType = "gcode" };
            _ = db.Set<FolderNode>().Add(folder);
            var gcode = new GcodeFile
            {
                Id = Guid.NewGuid(),
                Name = "combo.gcode",
                FileName = "combo.gcode",
                FolderId = folder.Id,
                FilePath = "/tmp",
                FileHash = "hash",
                FileSizeBytes = 1,
                UploadedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _ = db.Set<GcodeFile>().Add(gcode);
            var project = new PrintProject
            {
                Id = Guid.NewGuid(),
                Name = "combo-proj",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _ = db.Set<PrintProject>().Add(project);
            var projectFile = new PrintProjectFile
            {
                Id = Guid.NewGuid(),
                PrintProjectId = project.Id,
                GcodeFileId = gcode.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _ = db.Set<PrintProjectFile>().Add(projectFile);

            var projectSku = new PartInventory
            {
                Id = Guid.NewGuid(),
                Sku = "PF-PROJECT-01",
                Name = "Project SKU",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            var gcodeSku = new PartInventory
            {
                Id = Guid.NewGuid(),
                Sku = "PF-GCODE-01",
                Name = "Gcode SKU",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.PartInventories.AddRange(projectSku, gcodeSku);

            _ = db.PartOutputMappings.Add(new PartOutputMapping
            {
                Id = Guid.NewGuid(),
                PartInventoryId = projectSku.Id,
                PrintProjectFileId = projectFile.Id,
                Quantity = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = db.PartOutputMappings.Add(new PartOutputMapping
            {
                Id = Guid.NewGuid(),
                PartInventoryId = gcodeSku.Id,
                GcodeFileId = gcode.Id,
                Quantity = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            var job = new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "combo.gcode",
                Status = PrintJobStatus.Completed,
                Copies = 1,
                ProjectFileId = projectFile.Id,
                GcodeFileId = gcode.Id,
            };
            _ = db.PrintJobs.Add(job);
            _ = await db.SaveChangesAsync();

            projectSkuId = projectSku.Id;
            gcodeSkuId = gcodeSku.Id;
            jobId = job.Id;
        }

        PartHarvestService sut = CreateSut();
        HarvestResult result = await sut.HarvestJobAsync(jobId, new HarvestJobRequest(), null);

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        await using var db2 = new AppDbContext(_options);
        Assert.Equal(1, (await db2.PartInventories.SingleAsync(p => p.Id == projectSkuId)).OnHand);
        Assert.Equal(0, (await db2.PartInventories.SingleAsync(p => p.Id == gcodeSkuId)).OnHand);
    }

    [Fact]
    public async Task HarvestJobAsync_IdempotentReplay_DoesNotDoubleApply()
    {
        (PrintJob job, PartInventory part) = await SeedCompletedJobWithMappingAsync(copies: 1, mappingQuantity: 2);
        PartHarvestService sut = CreateSut();

        HarvestResult first = await sut.HarvestJobAsync(job.Id, new HarvestJobRequest(), "u1");
        HarvestResult second = await sut.HarvestJobAsync(job.Id, new HarvestJobRequest(), "u1");

        Assert.Equal(PartInventoryOutcome.Ok, first.Outcome);
        Assert.Equal(PartInventoryOutcome.IdempotentReplay, second.Outcome);
        Assert.True(second.Response!.AlreadyHarvested);

        await using var db = new AppDbContext(_options);
        Assert.Equal(2, (await db.PartInventories.SingleAsync(p => p.Id == part.Id)).OnHand);
        _ = Assert.Single(await db.PartInventoryAdjustments.ToListAsync());
    }

    [Fact]
    public async Task HarvestJobAsync_ConcurrentDuplicates_ProduceExactlyOneCommit_NoConflict()
    {
        (PrintJob job, PartInventory part) = await SeedCompletedJobWithMappingAsync(copies: 1, mappingQuantity: 4);
        PartHarvestService sut = CreateSut();
        const int callers = 5;

        HarvestResult[] results = await Task.WhenAll(Enumerable.Range(0, callers)
            .Select(_ => sut.HarvestJobAsync(job.Id, new HarvestJobRequest(), "u1")));

        // No caller may receive a Conflict — a same-job race must fold into an
        // IdempotentReplay against committed state, never a bare 409.
        Assert.All(results, r => Assert.True(
            r.Outcome == PartInventoryOutcome.Ok || r.Outcome == PartInventoryOutcome.IdempotentReplay,
            $"Unexpected outcome {r.Outcome}: {r.Message}"));

        int oks = results.Count(r => r.Outcome == PartInventoryOutcome.Ok);
        Assert.Equal(1, oks);

        await using var db = new AppDbContext(_options);
        Assert.Equal(4, (await db.PartInventories.SingleAsync(p => p.Id == part.Id)).OnHand);
        _ = Assert.Single(await db.PartInventoryAdjustments.ToListAsync());

        // Every replay must expose the prior harvest metadata rather than a null response.
        IEnumerable<HarvestResult> replays = results.Where(r => r.Outcome == PartInventoryOutcome.IdempotentReplay);
        Assert.All(replays, r =>
        {
            Assert.NotNull(r.Response);
            Assert.True(r.Response!.AlreadyHarvested);
            _ = Assert.Single(r.Response.Adjustments);
        });
    }

    [Fact]
    public async Task HarvestJobAsync_UnknownBinCode_ReturnsBinNotFoundAndDoesNotStamp()
    {
        (PrintJob job, _) = await SeedCompletedJobWithMappingAsync();

        PartHarvestService sut = CreateSut();
        HarvestResult result = await sut.HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(BinCode: "NOPE"),
            null);

        Assert.Equal(PartInventoryOutcome.BinNotFound, result.Outcome);
        await using var db = new AppDbContext(_options);
        Assert.Null((await db.PrintJobs.SingleAsync(j => j.Id == job.Id)).HarvestedAt);
        Assert.Empty(await db.PartInventoryAdjustments.ToListAsync());
    }

    [Fact]
    public async Task HarvestJobAsync_NoMappingsAndNoOutputs_ReturnsNoMappings()
    {
        Guid jobId;
        await using (var db = new AppDbContext(_options))
        {
            var job = new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "unmapped.gcode",
                Status = PrintJobStatus.Completed,
                Copies = 1,
                GcodeFileId = Guid.NewGuid(),
            };
            _ = db.PrintJobs.Add(job);
            _ = await db.SaveChangesAsync();
            jobId = job.Id;
        }

        PartHarvestService sut = CreateSut();
        HarvestResult result = await sut.HarvestJobAsync(jobId, new HarvestJobRequest(), null);

        Assert.Equal(PartInventoryOutcome.NoMappings, result.Outcome);
        await using var db2 = new AppDbContext(_options);
        Assert.Null((await db2.PrintJobs.SingleAsync(j => j.Id == jobId)).HarvestedAt);
    }
}
