using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Idempotency;
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
        bool useProjectFile = false,
        bool assignDefaultBin = true)
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
            FilePath = "/tests",
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

        Bin? defaultBin = null;
        if (assignDefaultBin)
        {
            defaultBin = new Bin
            {
                Id = Guid.NewGuid(),
                Code = ($"BIN-{Guid.NewGuid():N}"[..16]).ToUpperInvariant(),
                Name = "Default",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _ = db.Bins.Add(defaultBin);
        }

        var part = new PartInventory
        {
            Id = Guid.NewGuid(),
            Sku = "PF-BRKT-01",
            Name = "Bracket",
            DefaultBinId = defaultBin?.Id,
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
        Assert.Equal(6, ledger[0].ResultingBalance);
        Assert.Equal(PartAdjustmentReason.Harvest, ledger[0].Reason);
        Assert.Equal(job.Id, ledger[0].PrintJobId);
    }

    [Fact]
    public async Task HarvestJobAsync_ExplicitOutputs_TakePrecedenceOverMappings()
    {
        (PrintJob job, PartInventory bracket) = await SeedCompletedJobWithMappingAsync(copies: 2, mappingQuantity: 3);

        PartHarvestService sut = CreateSut();
        HarvestResult result = await sut.HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(
                Outputs: [new HarvestOutputRequestItem(bracket.Sku, 4)],
                OverrideReason: "Operator verified four good parts."),
            null);

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        await using var db2 = new AppDbContext(_options);
        Assert.Equal(4, (await db2.PartInventories.SingleAsync(p => p.Id == bracket.Id)).OnHand);
        PartHarvestOutputSnapshot snapshot = await db2.PartHarvestOutputSnapshots.SingleAsync();
        Assert.Equal(PartHarvestOutputOrigin.ExplicitOutputs, snapshot.Origin);
        Assert.True(snapshot.OverrideApplied);
        Assert.Equal("Operator verified four good parts.", snapshot.OverrideReason);
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
                FilePath = "/tests",
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
            var defaultBin = new Bin
            {
                Id = Guid.NewGuid(),
                Code = "BIN-COMBO",
                Name = "Combo",
                IsActive = true,
            };
            _ = db.Bins.Add(defaultBin);
            projectSku.DefaultBinId = defaultBin.Id;
            gcodeSku.DefaultBinId = defaultBin.Id;
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
        HarvestOutputResponse firstOutput = Assert.Single(first.Response!.Outputs);
        HarvestOutputResponse replayOutput = Assert.Single(second.Response.Outputs);
        Assert.Equal(firstOutput, replayOutput);

        await using var db = new AppDbContext(_options);
        Assert.Equal(2, (await db.PartInventories.SingleAsync(p => p.Id == part.Id)).OnHand);
        _ = Assert.Single(await db.PartInventoryAdjustments.ToListAsync());
        _ = Assert.Single(await db.PartHarvestOutputSnapshots.ToListAsync());
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
            _ = Assert.Single(r.Response.Outputs);
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

    [Fact]
    public async Task HarvestJobAsync_QuantityOverride_ReplacesCopyMultiplier()
    {
        (PrintJob job, PartInventory part) = await SeedCompletedJobWithMappingAsync(copies: 2, mappingQuantity: 3);

        HarvestResult result = await CreateSut().HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(QuantityOverride: 4),
            null);

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        await using var db = new AppDbContext(_options);
        Assert.Equal(12, (await db.PartInventories.SingleAsync(p => p.Id == part.Id)).OnHand);
    }

    [Fact]
    public async Task HarvestJobAsync_ExplicitOutputsWithQuantityOverride_ReturnsInvalidWithoutWrites()
    {
        (PrintJob job, PartInventory part) = await SeedCompletedJobWithMappingAsync();

        HarvestResult result = await CreateSut().HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(
                QuantityOverride: 2,
                Outputs: [new HarvestOutputRequestItem(part.Sku, 1)]),
            null);

        Assert.Equal(PartInventoryOutcome.InvalidRequest, result.Outcome);
        await using var db = new AppDbContext(_options);
        Assert.Equal(0, (await db.PartInventories.SingleAsync(p => p.Id == part.Id)).OnHand);
        Assert.Null((await db.PrintJobs.SingleAsync(j => j.Id == job.Id)).HarvestedAt);
        Assert.Empty(await db.PartInventoryAdjustments.ToListAsync());
    }

    [Fact]
    public async Task HarvestJobAsync_MultipleMappedOutputs_CommitsEachSkuOnce()
    {
        (PrintJob job, PartInventory first) = await SeedCompletedJobWithMappingAsync(copies: 2, mappingQuantity: 2);
        Guid secondId;
        await using (var db = new AppDbContext(_options))
        {
            var second = new PartInventory
            {
                Id = Guid.NewGuid(),
                Sku = "PF-SECOND-01",
                Name = "Second",
                DefaultBinId = first.DefaultBinId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _ = db.PartInventories.Add(second);
            _ = db.PartOutputMappings.Add(new PartOutputMapping
            {
                Id = Guid.NewGuid(),
                PartInventoryId = second.Id,
                GcodeFileId = job.GcodeFileId,
                Quantity = 3,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = await db.SaveChangesAsync();
            secondId = second.Id;
        }

        HarvestResult result = await CreateSut().HarvestJobAsync(job.Id, new HarvestJobRequest(), "actor");

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        Assert.Equal(2, result.Response!.Adjustments.Count);
        await using var verify = new AppDbContext(_options);
        Assert.Equal(4, (await verify.PartInventories.SingleAsync(p => p.Id == first.Id)).OnHand);
        Assert.Equal(6, (await verify.PartInventories.SingleAsync(p => p.Id == secondId)).OnHand);
        Assert.Equal(2, await verify.PartInventoryAdjustments.CountAsync());
    }

    [Fact]
    public async Task HarvestJobAsync_WrongBin_ReturnsExpectedAndActualBeforeMutation()
    {
        (PrintJob job, PartInventory part) = await SeedCompletedJobWithMappingAsync();
        await using (var db = new AppDbContext(_options))
        {
            var expected = new Bin { Id = Guid.NewGuid(), Code = "BIN-A", Name = "A", IsActive = true };
            var actual = new Bin { Id = Guid.NewGuid(), Code = "BIN-B", Name = "B", IsActive = true };
            db.Bins.AddRange(expected, actual);
            PartInventory tracked = await db.PartInventories.SingleAsync(p => p.Id == part.Id);
            tracked.DefaultBinId = expected.Id;
            _ = await db.SaveChangesAsync();
        }

        HarvestResult result = await CreateSut().HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(BinCode: "bin-b"),
            null);

        Assert.Equal(PartInventoryOutcome.WrongBin, result.Outcome);
        WrongBinMismatchResponse mismatch = Assert.Single(result.WrongBin!.Mismatches);
        Assert.Equal(part.Sku, mismatch.PartSku);
        Assert.Equal("BIN-A", mismatch.ExpectedBinCode);
        Assert.Equal("BIN-B", mismatch.ScannedBinCode);
        await using var verify = new AppDbContext(_options);
        Assert.Equal(0, (await verify.PartInventories.SingleAsync(p => p.Id == part.Id)).OnHand);
        Assert.Null((await verify.PrintJobs.SingleAsync(j => j.Id == job.Id)).HarvestedAt);
        Assert.Empty(await verify.PartInventoryAdjustments.ToListAsync());
        Assert.Empty(await verify.PartHarvestOutputSnapshots.ToListAsync());
        BarcodeScanLog scan = await verify.BarcodeScanLogs.SingleAsync();
        Assert.Equal(BarcodeScanOutcome.WrongBin, scan.Outcome);
        Assert.Equal(part.Id, scan.PartInventoryId);
    }

    [Fact]
    public async Task HarvestJobAsync_OmittedBin_UsesCommonDefaultBin()
    {
        (PrintJob job, PartInventory part) = await SeedCompletedJobWithMappingAsync();
        Guid binId = part.DefaultBinId!.Value;

        HarvestResult result = await CreateSut().HarvestJobAsync(job.Id, new HarvestJobRequest(), null);

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        Assert.Equal(binId, result.Response!.BinId);
        Assert.NotNull(result.Response.BinCode);
    }

    [Fact]
    public async Task HarvestJobAsync_NoSuppliedOrDefaultBin_ReturnsBinNotFoundWithoutMutation()
    {
        (PrintJob job, PartInventory part) = await SeedCompletedJobWithMappingAsync(assignDefaultBin: false);

        HarvestResult result = await CreateSut().HarvestJobAsync(job.Id, new HarvestJobRequest(), "actor");

        Assert.Equal(PartInventoryOutcome.BinNotFound, result.Outcome);
        await using var verify = new AppDbContext(_options);
        Assert.Equal(0, (await verify.PartInventories.SingleAsync(value => value.Id == part.Id)).OnHand);
        Assert.Null((await verify.PrintJobs.SingleAsync(value => value.Id == job.Id)).HarvestedAt);
        Assert.Empty(await verify.PartInventoryAdjustments.ToListAsync());
        Assert.Empty(await verify.BarcodeScanLogs.ToListAsync());
    }

    [Fact]
    public async Task HarvestJobAsync_LaterOutputOverflows_RollsBackEntireHarvest()
    {
        (PrintJob job, PartInventory first) = await SeedCompletedJobWithMappingAsync();
        Guid overflowId;
        await using (var db = new AppDbContext(_options))
        {
            var overflow = new PartInventory
            {
                Id = Guid.NewGuid(),
                Sku = "ZZ-OVERFLOW",
                Name = "Overflow",
                DefaultBinId = first.DefaultBinId,
                OnHand = int.MaxValue,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _ = db.PartInventories.Add(overflow);
            _ = db.PartOutputMappings.Add(new PartOutputMapping
            {
                Id = Guid.NewGuid(),
                PartInventoryId = overflow.Id,
                GcodeFileId = job.GcodeFileId,
                Quantity = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = await db.SaveChangesAsync();
            overflowId = overflow.Id;
        }

        string binCode;
        await using (var lookup = new AppDbContext(_options))
        {
            binCode = await lookup.Bins
                .Where(value => value.Id == first.DefaultBinId)
                .Select(value => value.Code)
                .SingleAsync();
        }

        HarvestResult result = await CreateSut().HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(BinCode: binCode),
            null);

        Assert.Equal(PartInventoryOutcome.InvalidRequest, result.Outcome);
        await using var verify = new AppDbContext(_options);
        Assert.Equal(0, (await verify.PartInventories.SingleAsync(p => p.Id == first.Id)).OnHand);
        Assert.Equal(int.MaxValue, (await verify.PartInventories.SingleAsync(p => p.Id == overflowId)).OnHand);
        Assert.Null((await verify.PrintJobs.SingleAsync(j => j.Id == job.Id)).HarvestedAt);
        Assert.Empty(await verify.PartInventoryAdjustments.ToListAsync());
        Assert.Empty(await verify.PartHarvestOutputSnapshots.ToListAsync());
        Assert.Empty(await verify.BarcodeScanLogs.ToListAsync());
    }

    [Fact]
    public async Task HarvestJobAsync_ScannedBin_CommitsUnifiedScanHistoryWithLedger()
    {
        (PrintJob job, PartInventory part) = await SeedCompletedJobWithMappingAsync();
        string binCode;
        await using (var db = new AppDbContext(_options))
        {
            binCode = await db.Bins
                .Where(value => value.Id == part.DefaultBinId)
                .Select(value => value.Code)
                .SingleAsync();
        }

        HarvestResult result = await CreateSut().HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(BinCode: binCode),
            "operator-1");

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        await using var verify = new AppDbContext(_options);
        BarcodeScanLog scan = await verify.BarcodeScanLogs.SingleAsync();
        Assert.Equal(BarcodeScanAction.Harvest, scan.Action);
        Assert.Equal(BarcodeScanOutcome.Resolved, scan.Outcome);
        Assert.Equal(part.Id, scan.PartInventoryId);
        Assert.Equal(part.DefaultBinId, scan.BinId);
        Assert.Equal("operator-1", scan.UserId);
        Assert.Equal(1, (await verify.PartInventoryAdjustments.SingleAsync()).ResultingBalance);
    }

    [Fact]
    public async Task HarvestJobAsync_JobSnapshotWinsAfterMappingEdit()
    {
        (PrintJob job, PartInventory part) = await SeedCompletedJobWithMappingAsync(mappingQuantity: 2);
        await using (var db = new AppDbContext(_options))
        {
            PartOutputMapping mapping = await db.PartOutputMappings.SingleAsync();
            Bin bin = await db.Bins.SingleAsync(value => value.Id == part.DefaultBinId);
            _ = db.PrintJobPartOutputSnapshots.Add(new PrintJobPartOutputSnapshot
            {
                Id = Guid.NewGuid(),
                PrintJobId = job.Id,
                PartInventoryId = part.Id,
                Sku = part.Sku,
                QuantityPerPrint = 2,
                ExpectedBinId = bin.Id,
                ExpectedBinCode = bin.Code,
                SourceKind = PartOutputMappingSourceKind.GcodeFile,
                SourceFileId = job.GcodeFileId!.Value,
                SourceMappingId = mapping.Id,
                Sequence = 0,
                CreatedAt = DateTime.UtcNow,
            });
            mapping.Quantity = 9;
            _ = await db.SaveChangesAsync();
        }

        HarvestResult result = await CreateSut().HarvestJobAsync(job.Id, new HarvestJobRequest(), "actor");

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        HarvestOutputResponse output = Assert.Single(result.Response!.Outputs);
        Assert.Equal(2, output.Quantity);
        Assert.Equal(PartHarvestOutputOrigin.JobSnapshot, output.Origin);
        await using var verify = new AppDbContext(_options);
        Assert.Equal(2, (await verify.PartInventories.SingleAsync(value => value.Id == part.Id)).OnHand);
    }

    [Fact]
    public async Task HarvestJobAsync_TwoSkusDifferentBins_UsesPerSkuAssignmentsAtomically()
    {
        (PrintJob job, PartInventory first) = await SeedCompletedJobWithMappingAsync();
        string firstBinCode;
        Guid secondId;
        const string SecondBinCode = "BIN-SECOND";
        await using (var db = new AppDbContext(_options))
        {
            firstBinCode = await db.Bins
                .Where(value => value.Id == first.DefaultBinId)
                .Select(value => value.Code)
                .SingleAsync();
            var secondBin = new Bin
            {
                Id = Guid.NewGuid(),
                Code = SecondBinCode,
                Name = "Second",
                IsActive = true,
            };
            var second = new PartInventory
            {
                Id = Guid.NewGuid(),
                Sku = "SKU-SECOND-BIN",
                Name = "Second",
                DefaultBinId = secondBin.Id,
                IsActive = true,
            };
            _ = db.Bins.Add(secondBin);
            _ = db.PartInventories.Add(second);
            _ = db.PartOutputMappings.Add(new PartOutputMapping
            {
                Id = Guid.NewGuid(),
                PartInventoryId = second.Id,
                GcodeFileId = job.GcodeFileId,
                Quantity = 2,
            });
            _ = await db.SaveChangesAsync();
            secondId = second.Id;
        }

        HarvestResult missingAssignments = await CreateSut().HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(),
            "actor");
        Assert.Equal(PartInventoryOutcome.InvalidRequest, missingAssignments.Outcome);

        HarvestResult result = await CreateSut().HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(OutputBins:
            [
                new HarvestOutputBinRequest(first.Sku, firstBinCode),
                new HarvestOutputBinRequest("SKU-SECOND-BIN", SecondBinCode),
            ]),
            "actor");

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        Assert.Null(result.Response!.BinId);
        Assert.Equal(2, result.Response.Outputs.Count);
        Assert.Contains(result.Response.Outputs, output =>
            output.PartSku == first.Sku && output.ActualBinCode == firstBinCode);
        Assert.Contains(result.Response.Outputs, output =>
            output.PartSku == "SKU-SECOND-BIN" && output.ActualBinCode == SecondBinCode);
        await using var verify = new AppDbContext(_options);
        Assert.Equal(1, (await verify.PartInventories.SingleAsync(value => value.Id == first.Id)).OnHand);
        Assert.Equal(2, (await verify.PartInventories.SingleAsync(value => value.Id == secondId)).OnHand);
        Assert.Equal(2, await verify.PartInventoryAdjustments.CountAsync());
        Assert.Equal(2, await verify.PartHarvestOutputSnapshots.CountAsync());
    }

    [Fact]
    public async Task HarvestJobAsync_WrongBinOverride_RequiresReasonAndPersistsAudit()
    {
        (PrintJob job, PartInventory part) = await SeedCompletedJobWithMappingAsync();
        await using (var db = new AppDbContext(_options))
        {
            _ = db.Bins.Add(new Bin
            {
                Id = Guid.NewGuid(),
                Code = "BIN-WRONG-OVERRIDE",
                Name = "Wrong",
                IsActive = true,
            });
            _ = await db.SaveChangesAsync();
        }

        HarvestResult missingReason = await CreateSut().HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(
                BinCode: "BIN-WRONG-OVERRIDE",
                AllowWrongBin: true,
                OverrideReason: " "),
            "actor");
        Assert.Equal(PartInventoryOutcome.InvalidRequest, missingReason.Outcome);

        HarvestResult overridden = await CreateSut().HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(
                BinCode: "BIN-WRONG-OVERRIDE",
                AllowWrongBin: true,
                OverrideReason: "Bin A is temporarily unavailable."),
            "actor");

        Assert.Equal(PartInventoryOutcome.Ok, overridden.Outcome);
        HarvestOutputResponse output = Assert.Single(overridden.Response!.Outputs);
        Assert.Equal(part.Sku, output.PartSku);
        Assert.NotEqual(output.ExpectedBinCode, output.ActualBinCode);
        Assert.True(output.OverrideApplied);
        Assert.Equal("Bin A is temporarily unavailable.", output.OverrideReason);
        await using var verify = new AppDbContext(_options);
        PartHarvestOutputSnapshot snapshot = await verify.PartHarvestOutputSnapshots.SingleAsync();
        Assert.True(snapshot.OverrideApplied);
        Assert.Equal("Bin A is temporarily unavailable.", snapshot.OverrideReason);
        Assert.Equal(2, await verify.BarcodeScanLogs.CountAsync(
            value => value.Outcome == BarcodeScanOutcome.WrongBin));
    }

    [Fact]
    public async Task HarvestJobAsync_PartialExplicitOutputs_AreRejectedBeforeMutation()
    {
        (PrintJob job, PartInventory first) = await SeedCompletedJobWithMappingAsync();
        await using (var db = new AppDbContext(_options))
        {
            var second = new PartInventory
            {
                Id = Guid.NewGuid(),
                Sku = "SKU-PARTIAL-SECOND",
                Name = "Second",
                DefaultBinId = first.DefaultBinId,
                IsActive = true,
            };
            _ = db.PartInventories.Add(second);
            _ = db.PartOutputMappings.Add(new PartOutputMapping
            {
                Id = Guid.NewGuid(),
                PartInventoryId = second.Id,
                GcodeFileId = job.GcodeFileId,
                Quantity = 1,
            });
            _ = await db.SaveChangesAsync();
        }

        HarvestResult result = await CreateSut().HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(
                Outputs: [new HarvestOutputRequestItem(first.Sku, 1)],
                OverrideReason: "Incorrectly omitted second output."),
            "actor");

        Assert.Equal(PartInventoryOutcome.InvalidRequest, result.Outcome);
        await using var verify = new AppDbContext(_options);
        Assert.Null((await verify.PrintJobs.SingleAsync(value => value.Id == job.Id)).HarvestedAt);
        Assert.Empty(await verify.PartInventoryAdjustments.ToListAsync());
        Assert.Empty(await verify.PartHarvestOutputSnapshots.ToListAsync());
    }

    [Fact]
    public async Task HarvestJobAsync_DuplicateOutputBinSku_IsRejected()
    {
        (PrintJob job, PartInventory part) = await SeedCompletedJobWithMappingAsync();
        string binCode;
        await using (var db = new AppDbContext(_options))
        {
            binCode = await db.Bins
                .Where(value => value.Id == part.DefaultBinId)
                .Select(value => value.Code)
                .SingleAsync();
        }

        HarvestResult result = await CreateSut().HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(OutputBins:
            [
                new HarvestOutputBinRequest(part.Sku, binCode),
                new HarvestOutputBinRequest(part.Sku.ToLowerInvariant(), binCode),
            ]),
            "actor");

        Assert.Equal(PartInventoryOutcome.InvalidRequest, result.Outcome);
    }

    [Fact]
    public async Task HarvestJobAsync_ExplicitOutputsWithoutMapping_SupplyCompleteFinalSet()
    {
        Guid jobId;
        Guid partId;
        await using (var db = new AppDbContext(_options))
        {
            var bin = new Bin
            {
                Id = Guid.NewGuid(),
                Code = "BIN-MANUAL",
                Name = "Manual",
                IsActive = true,
            };
            var part = new PartInventory
            {
                Id = Guid.NewGuid(),
                Sku = "SKU-MANUAL",
                Name = "Manual",
                DefaultBinId = bin.Id,
                IsActive = true,
            };
            var job = new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "legacy-external.gcode",
                Status = PrintJobStatus.Completed,
            };
            _ = db.Bins.Add(bin);
            _ = db.PartInventories.Add(part);
            _ = db.PrintJobs.Add(job);
            _ = await db.SaveChangesAsync();
            jobId = job.Id;
            partId = part.Id;
        }

        HarvestResult result = await CreateSut().HarvestJobAsync(
            jobId,
            new HarvestJobRequest(
                Outputs: [new HarvestOutputRequestItem("SKU-MANUAL", 3)],
                OverrideReason: "Legacy job manually identified."),
            "actor");

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);
        Assert.Equal(PartHarvestOutputOrigin.ExplicitOutputs, Assert.Single(result.Response!.Outputs).Origin);
        await using var verify = new AppDbContext(_options);
        Assert.Equal(3, (await verify.PartInventories.SingleAsync(value => value.Id == partId)).OnHand);
    }

    [Fact]
    public async Task HarvestJobAsync_FeatureDisabled_DoesNotOpenDatabase()
    {
        var factory = new Mock<IDbContextFactory<AppDbContext>>(MockBehavior.Strict);
        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Strict);
        gate.Setup(value => value.IsEnabled(OperatorFeature.PrintedPartsInventory)).Returns(false);
        gate.Setup(value => value.IsEnabledAsync(OperatorFeature.PrintedPartsInventory, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sut = new PartHarvestService(
            factory.Object,
            NullLogger<PartHarvestService>.Instance,
            attentionBroadcaster: null,
            gate.Object);

        HarvestResult result = await sut.HarvestJobAsync(Guid.NewGuid(), new HarvestJobRequest(), "actor");

        Assert.Equal(PartInventoryOutcome.FeatureDisabled, result.Outcome);
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HarvestJobAsync_Success_BroadcastsResolvedOnceAfterCommit()
    {
        (PrintJob job, _) = await SeedCompletedJobWithMappingAsync();
        var broadcaster = new Mock<IAttentionBroadcaster>(MockBehavior.Strict);
        broadcaster
            .Setup(value => value.NotifyChangedAsync(
                It.Is<AttentionChangedPayload>(payload =>
                    payload.ItemId == AttentionIdPrefixes.Build(AttentionIdPrefixes.Harvest, job.Id)
                    && payload.ChangeKind == AttentionChangeKind.Resolved),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var sut = new PartHarvestService(_factory, NullLogger<PartHarvestService>.Instance, broadcaster.Object);

        HarvestResult first = await sut.HarvestJobAsync(job.Id, new HarvestJobRequest(), "actor");
        HarvestResult replay = await sut.HarvestJobAsync(job.Id, new HarvestJobRequest(), "actor");

        Assert.Equal(PartInventoryOutcome.Ok, first.Outcome);
        Assert.Equal(PartInventoryOutcome.IdempotentReplay, replay.Outcome);
        broadcaster.VerifyAll();
    }

    // --- issue #715 r8 blocker B1/H3: harvest endpoint reserved-prefix guard ----------------------
    // A client-supplied harvest operationKey is written verbatim to PrintJob.HarvestOperationKey,
    // which shares its unique filtered index with the server's autogenerated "harvest:<jobId>" keys.
    // If a client could pre-occupy jobB's future server key, jobB's later keyless harvest would hit a
    // unique violation and break permanently. The service guard (mirroring PartInventoryService) must
    // reject the reserved idem:/harvest: namespaces — case- and width-insensitive (NFKC) — and write
    // nothing. The DTO's [ReservedOperationKeyPrefix] attribute enforces the same at the API boundary
    // (covered by ReservedOperationKeyPrefixAttributeTests).

    [Theory]
    [InlineData("harvest:foo")]                                             // reserved harvest namespace
    [InlineData("Harvest:FOO")]                                             // case-insensitive
    [InlineData("idem:foo")]                                                // reserved idem namespace
    [InlineData("\uFF48\uFF41\uFF52\uFF56\uFF45\uFF53\uFF54\uFF1A\uFF46\uFF4F\uFF4F")] // fullwidth ｈａｒｖｅｓｔ：ｆｏｏ → NFKC harvest:foo
    public async Task HarvestJobAsync_ClientSuppliedReservedOperationKey_RejectedAndWritesNothing(
        string reservedKey)
    {
        (PrintJob job, PartInventory part) = await SeedCompletedJobWithMappingAsync(copies: 2, mappingQuantity: 3);

        PartHarvestService sut = CreateSut();
        HarvestResult result = await sut.HarvestJobAsync(job.Id, new HarvestJobRequest(OperationKey: reservedKey), "op-1");

        Assert.Equal(PartInventoryOutcome.InvalidRequest, result.Outcome);
        Assert.Null(result.Response);
        Assert.NotNull(result.Message);
        Assert.Contains(IdempotencyKeyUtilities.SynthesizedOperationKeyPrefix, result.Message!, StringComparison.Ordinal);
        Assert.Contains(IdempotencyKeyUtilities.HarvestOperationKeyPrefix, result.Message!, StringComparison.Ordinal);

        // The rejected request must not touch stock, stamp the job, or append a ledger row.
        await using var db = new AppDbContext(_options);
        PartInventory refreshedPart = await db.PartInventories.SingleAsync(p => p.Id == part.Id);
        Assert.Equal(0, refreshedPart.OnHand);

        PrintJob refreshedJob = await db.PrintJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Null(refreshedJob.HarvestedAt);
        Assert.Null(refreshedJob.HarvestOperationKey);
        Assert.Null(refreshedJob.HarvestedByUserId);

        Assert.Empty(await db.PartInventoryAdjustments.ToListAsync());
    }

    [Fact]
    public async Task HarvestJobAsync_ClientSuppliedNonReservedOperationKey_PersistedVerbatim()
    {
        // "harvestable-tote" shares a stem with the reserved "harvest:" prefix but is NOT in the
        // reserved namespace (no colon delimiter), so it must be accepted and stored unchanged.
        (PrintJob job, PartInventory part) = await SeedCompletedJobWithMappingAsync(copies: 2, mappingQuantity: 3);

        PartHarvestService sut = CreateSut();
        HarvestResult result = await sut.HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(OperationKey: "harvestable-tote"),
            "op-1");

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);

        await using var db = new AppDbContext(_options);
        PrintJob refreshedJob = await db.PrintJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal("harvestable-tote", refreshedJob.HarvestOperationKey);
        Assert.Equal(6, (await db.PartInventories.SingleAsync(p => p.Id == part.Id)).OnHand);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HarvestJobAsync_MissingOperationKey_AutogeneratesServerHarvestKey(string? operationKey)
    {
        // Null/empty/whitespace normalizes to null, so the server autogenerates "harvest:<jobId>".
        // The guard must not reject this path — the reserved prefix is only forbidden on client input.
        (PrintJob job, _) = await SeedCompletedJobWithMappingAsync(copies: 2, mappingQuantity: 3);

        PartHarvestService sut = CreateSut();
        HarvestResult result = await sut.HarvestJobAsync(
            job.Id,
            new HarvestJobRequest(OperationKey: operationKey),
            "op-1");

        Assert.Equal(PartInventoryOutcome.Ok, result.Outcome);

        await using var db = new AppDbContext(_options);
        PrintJob refreshedJob = await db.PrintJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal($"harvest:{job.Id:N}", refreshedJob.HarvestOperationKey);
    }
}
