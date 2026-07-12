using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Web.Api.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PartsInventory;

public sealed class PartOutputSnapshotServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public PartOutputSnapshotServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        using var db = new AppDbContext(_options);
        _ = db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task CaptureJobSnapshotIfAbsentAsync_MappingChangesAndRetry_DoNotOverwriteRows()
    {
        (Guid jobId, Guid mappingId, Guid partId, Guid binId) = await SeedMappedJobAsync();
        await using (var db = new AppDbContext(_options))
        {
            PrintJob job = await db.PrintJobs.SingleAsync(value => value.Id == jobId);
            PartOutputSnapshotService service = CreateService(db, enabled: true);

            Assert.True(await service.CaptureJobSnapshotIfAbsentAsync(job));
            _ = await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(_options))
        {
            PartOutputMapping mapping = await db.PartOutputMappings.SingleAsync(value => value.Id == mappingId);
            mapping.Quantity = 9;
            PartInventory part = await db.PartInventories.SingleAsync(value => value.Id == partId);
            part.DefaultBinId = null;
            _ = await db.SaveChangesAsync();

            PrintJob job = await db.PrintJobs.SingleAsync(value => value.Id == jobId);
            PartOutputSnapshotService service = CreateService(db, enabled: true);
            Assert.False(await service.CaptureJobSnapshotIfAbsentAsync(job));
            _ = await db.SaveChangesAsync();
        }

        await using var verify = new AppDbContext(_options);
        PrintJobPartOutputSnapshot snapshot = await verify.PrintJobPartOutputSnapshots.SingleAsync();
        Assert.Equal("SKU-SNAPSHOT", snapshot.Sku);
        Assert.Equal(2, snapshot.QuantityPerPrint);
        Assert.Equal(binId, snapshot.ExpectedBinId);
        Assert.Equal("BIN-SNAPSHOT", snapshot.ExpectedBinCode);
        Assert.Equal(PartOutputMappingSourceKind.GcodeFile, snapshot.SourceKind);
        Assert.Equal(mappingId, snapshot.SourceMappingId);
        Assert.Equal(0, snapshot.Sequence);
    }

    [Fact]
    public async Task CaptureJobSnapshotIfAbsentAsync_NoMapping_LeavesSnapshotAbsent()
    {
        Guid jobId;
        await using (var db = new AppDbContext(_options))
        {
            var job = new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = "legacy.gcode",
                Status = PrintJobStatus.Queued,
            };
            _ = db.PrintJobs.Add(job);
            _ = await db.SaveChangesAsync();
            jobId = job.Id;
        }

        await using (var db = new AppDbContext(_options))
        {
            PrintJob job = await db.PrintJobs.SingleAsync(value => value.Id == jobId);
            Assert.False(await CreateService(db, enabled: true).CaptureJobSnapshotIfAbsentAsync(job));
            _ = await db.SaveChangesAsync();
        }

        await using var verify = new AppDbContext(_options);
        Assert.Empty(await verify.PrintJobPartOutputSnapshots.ToListAsync());
    }

    [Fact]
    public async Task CaptureJobSnapshotIfAbsentAsync_FeatureDisabled_DoesNotReadOrWrite()
    {
        await using var db = new AppDbContext(_options);
        var job = new PrintJob { Id = Guid.NewGuid(), Name = "disabled.gcode" };

        Assert.False(await CreateService(db, enabled: false).CaptureJobSnapshotIfAbsentAsync(job));
        Assert.Empty(db.ChangeTracker.Entries<PrintJobPartOutputSnapshot>());
    }

    [Fact]
    public async Task SaveChangesAsync_ModifyingOrDeletingOutputSnapshots_IsRejected()
    {
        (Guid jobId, _, Guid partId, Guid binId) = await SeedMappedJobAsync();
        await using (var db = new AppDbContext(_options))
        {
            PrintJob job = await db.PrintJobs.SingleAsync(value => value.Id == jobId);
            _ = await CreateService(db, enabled: true).CaptureJobSnapshotIfAbsentAsync(job);
            _ = await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(_options))
        {
            PrintJobPartOutputSnapshot snapshot =
                await db.PrintJobPartOutputSnapshots.SingleAsync();
            snapshot.QuantityPerPrint = 99;
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using (var db = new AppDbContext(_options))
        {
            PrintJobPartOutputSnapshot snapshot =
                await db.PrintJobPartOutputSnapshots.SingleAsync();
            _ = db.PrintJobPartOutputSnapshots.Remove(snapshot);
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using (var db = new AppDbContext(_options))
        {
            PartInventory part = await db.PartInventories.SingleAsync(value => value.Id == partId);
            Bin bin = await db.Bins.SingleAsync(value => value.Id == binId);
            var adjustment = new PartInventoryAdjustment
            {
                Id = Guid.NewGuid(),
                PartInventoryId = part.Id,
                BinId = bin.Id,
                Delta = 1,
                ResultingBalance = 1,
                Reason = PartAdjustmentReason.Harvest,
                PrintJobId = jobId,
            };
            _ = db.PartInventoryAdjustments.Add(adjustment);
            _ = db.PartHarvestOutputSnapshots.Add(new PartHarvestOutputSnapshot
            {
                Id = Guid.NewGuid(),
                PrintJobId = jobId,
                PartInventoryId = part.Id,
                PartInventoryAdjustmentId = adjustment.Id,
                Sku = part.Sku,
                Quantity = 1,
                ActualBinId = bin.Id,
                ActualBinCode = bin.Code,
                Origin = PartHarvestOutputOrigin.JobSnapshot,
                Sequence = 0,
            });
            _ = await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(_options))
        {
            PartHarvestOutputSnapshot snapshot =
                await db.PartHarvestOutputSnapshots.SingleAsync();
            snapshot.OverrideReason = "tampered";
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
    }

    private static PartOutputSnapshotService CreateService(AppDbContext db, bool enabled)
    {
        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Strict);
        gate.Setup(value => value.IsEnabled(OperatorFeature.PrintedPartsInventory)).Returns(enabled);
        return new PartOutputSnapshotService(db, gate.Object);
    }

    private async Task<(Guid JobId, Guid MappingId, Guid PartId, Guid BinId)> SeedMappedJobAsync()
    {
        await using var db = new AppDbContext(_options);
        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            Path = "/snapshots",
            FolderType = "gcode",
        };
        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "snapshot.gcode",
            FileName = "snapshot.gcode",
            FilePath = "/snapshots",
            FolderId = folder.Id,
            FileHash = "snapshot",
            FileSizeBytes = 1,
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var bin = new Bin
        {
            Id = Guid.NewGuid(),
            Code = "BIN-SNAPSHOT",
            Name = "Snapshot",
            IsActive = true,
        };
        var part = new PartInventory
        {
            Id = Guid.NewGuid(),
            Sku = "SKU-SNAPSHOT",
            Name = "Snapshot Part",
            DefaultBinId = bin.Id,
            IsActive = true,
        };
        var mapping = new PartOutputMapping
        {
            Id = Guid.NewGuid(),
            PartInventoryId = part.Id,
            GcodeFileId = gcode.Id,
            Quantity = 2,
        };
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = gcode.Name,
            GcodeFileId = gcode.Id,
            Status = PrintJobStatus.Queued,
        };
        _ = db.Set<FolderNode>().Add(folder);
        _ = db.GcodeFiles.Add(gcode);
        _ = db.Bins.Add(bin);
        _ = db.PartInventories.Add(part);
        _ = db.PartOutputMappings.Add(mapping);
        _ = db.PrintJobs.Add(job);
        _ = await db.SaveChangesAsync();
        return (job.Id, mapping.Id, part.Id, bin.Id);
    }
}
