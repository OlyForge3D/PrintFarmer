using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Services.PartsInventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PartsInventory;

/// <summary>
/// Tests for <see cref="PartHarvestService"/>. Verifies the atomic
/// harvest workflow, the deterministic mapping precedence
/// (request outputs → project-file mapping → gcode-file mapping),
/// idempotent replay via the job's HarvestedAt stamp, and status
/// gating (only PrintJobStatus.Completed jobs may be harvested).
/// </summary>
public class PartHarvestServiceTests : IDisposable
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PartHarvestServiceTests()
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

    private PartHarvestService CreateSut() => new(_factory, NullLogger<PartHarvestService>.Instance);

    private async Task<(PrintJob Job, PartInventory Part)> SeedCompletedJobWithMappingAsync(
        int copies = 1,
        int mappingQuantity = 1,
        bool useProjectFile = false)
    {
        await using var db = new AppDbContext(_options);
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

        Guid? gcodeId = null;
        Guid? projectId = null;
        if (useProjectFile)
        {
            projectId = Guid.NewGuid();
        }
        else
        {
            gcodeId = Guid.NewGuid();
        }

        var mapping = new PartOutputMapping
        {
            Id = Guid.NewGuid(),
            PartInventoryId = part.Id,
            GcodeFileId = gcodeId,
            PrintProjectFileId = projectId,
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
            GcodeFileId = gcodeId,
            ProjectFileId = projectId,
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

            PartHarvestService sut = CreateSut();
            HarvestResult result = await sut.HarvestJobAsync(job.Id, new HarvestJobRequest(), null);
            Assert.Equal(PartInventoryOutcome.JobNotCompleted, result.Outcome);
        }
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
        Assert.Equal(6, refreshed.OnHand); // 2 copies * 3 mappingQuantity

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
            new HarvestJobRequest(
                Outputs: new[] { new HarvestOutputRequestItem("PF-CLIP-99", 4) }),
            null);

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        await using var db2 = new AppDbContext(_options);
        Assert.Equal(0, (await db2.PartInventories.SingleAsync(p => p.Id == bracket.Id)).OnHand);
        Assert.Equal(4, (await db2.PartInventories.SingleAsync(p => p.Id == altId)).OnHand);
    }

    [Fact]
    public async Task HarvestJobAsync_ProjectFileMapping_TakesPrecedenceOverGcodeMapping()
    {
        // Seed a job that has BOTH a project file and a gcode file, plus a mapping for each
        // pointing to different SKUs. Precedence: project file wins.
        Guid projectId = Guid.NewGuid();
        Guid gcodeId = Guid.NewGuid();
        Guid projectSkuId;
        Guid gcodeSkuId;
        Guid jobId;

        await using (var db = new AppDbContext(_options))
        {
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
                PrintProjectFileId = projectId,
                Quantity = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = db.PartOutputMappings.Add(new PartOutputMapping
            {
                Id = Guid.NewGuid(),
                PartInventoryId = gcodeSku.Id,
                GcodeFileId = gcodeId,
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
                ProjectFileId = projectId,
                GcodeFileId = gcodeId,
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
        Assert.Single(await db.PartInventoryAdjustments.ToListAsync());
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
