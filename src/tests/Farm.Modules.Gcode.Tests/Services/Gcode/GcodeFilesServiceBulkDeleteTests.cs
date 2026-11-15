using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.FileManagement;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Telemetry;
using Farm.Modules.Gcode.Services.Gcode;
using Farm.Testing.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit.Abstractions;

namespace Farm.Modules.Gcode.Tests.Services.Gcode;

/// <summary>
/// Bulk G-code deletion against a real <see cref="AppUnitOfWork"/> over SQLite in-memory, so the
/// set-based lookups, active-job predicate, historical reference clearing, direct mapping deletion,
/// owned transaction, and per-ID outcomes are exercised end to end with real stored files.
/// SQLite proves behaviour and query shape only; it says nothing about production provider latency.
/// </summary>
public sealed class GcodeFilesServiceBulkDeleteTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ITestOutputHelper _output;
    private readonly string _storageRoot;
    private readonly Guid _folderId = Guid.NewGuid();
    private readonly Guid _printerId = Guid.NewGuid();

    public GcodeFilesServiceBulkDeleteTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);
        _storageRoot = Path.Join(Path.GetTempPath(), "pfarm-gcode-bulk-delete", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_storageRoot);

        using AppDbContext db = NewDb();
        _ = db.Database.EnsureCreated();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        _ = db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "M" });
        _ = db.PrinterModels.Add(new PrinterModel { Id = modelId, Name = "PM", ManufacturerId = manufacturerId });
        _ = db.Printers.Add(new Printer { Id = _printerId, Name = "P", ServerUrl = "http://p", ManufacturerId = manufacturerId, ModelId = modelId });
        _ = db.Set<FolderNode>().Add(new FolderNode { Id = _folderId, Path = "/", FolderType = "gcode" });
        _ = db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_storageRoot))
        {
            RestoreWritable(_storageRoot);
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFilesAsync_MixedSelection_DeletesEligibleAndReportsEachDistinctIdInOrder()
    {
        GcodeFile deletable = await SeedFileAsync("a.gcode", withThumbnail: true);
        GcodeFile orphan = await SeedFileAsync("orphan.gcode", writeStoredFile: false);
        GcodeFile active = await SeedFileAsync("active.gcode");
        GcodeFile unsafePath = await SeedFileAsync("../escape.gcode", writeStoredFile: false);
        Guid missing = Guid.NewGuid();
        Guid historicalJobId = await SeedJobAsync(deletable.Id, PrintJobStatus.Completed);
        Guid activeJobId = await SeedJobAsync(active.Id, PrintJobStatus.Queued);
        (Guid directMappingId, Guid projectMappingId) = await SeedMappingsAsync(deletable.Id);

        IReadOnlyList<BulkDeleteItemResult> results = await DeleteAsync(
            [deletable.Id, missing, active.Id, deletable.Id, orphan.Id, unsafePath.Id]);

        results.Should().Equal(
            new BulkDeleteItemResult(deletable.Id, BulkDeleteItemStatus.Deleted),
            new BulkDeleteItemResult(missing, BulkDeleteItemStatus.NotFound),
            new BulkDeleteItemResult(active.Id, BulkDeleteItemStatus.Blocked, BulkDeleteItemReasons.ActiveJobs),
            new BulkDeleteItemResult(orphan.Id, BulkDeleteItemStatus.Deleted),
            new BulkDeleteItemResult(unsafePath.Id, BulkDeleteItemStatus.Blocked, BulkDeleteItemReasons.UnsafePath));

        await using AppDbContext db = NewDb();
        (await db.GcodeFiles.Select(f => f.Id).ToListAsync())
            .Should().BeEquivalentTo([active.Id, unsafePath.Id], "only blocked files keep their records");
        (await db.PrintJobs.SingleAsync(j => j.Id == historicalJobId)).GcodeFileId
            .Should().BeNull("historical jobs are kept with the deleted file reference cleared");
        (await db.PrintJobs.SingleAsync(j => j.Id == activeJobId)).GcodeFileId.Should().Be(active.Id);
        (await db.PartOutputMappings.AnyAsync(m => m.Id == directMappingId)).Should().BeFalse();
        (await db.PartOutputMappings.AnyAsync(m => m.Id == projectMappingId))
            .Should().BeFalse("indirect mappings are removed by the PrintProjectFile cascade, as before");
        File.Exists(StoredPath("a.gcode")).Should().BeFalse();
        File.Exists(StoredPath("a.png")).Should().BeFalse();
        File.Exists(StoredPath("active.gcode")).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteFilesAsync_QueryCountDoesNotGrowWithSelectionSize()
    {
        var selectsBySize = new Dictionary<int, int>();
        foreach (int size in new[] { 1, 10, 100 })
        {
            List<Guid> ids = [];
            for (int i = 0; i < size; i++)
            {
                ids.Add((await SeedFileAsync($"s{size}-{i}.gcode", withThumbnail: true)).Id);
            }

            var counter = new CommandCounter();
            var stopwatch = Stopwatch.StartNew();
            IReadOnlyList<BulkDeleteItemResult> results = await DeleteAsync(ids, counter);
            stopwatch.Stop();

            results.Should().OnlyContain(r => r.Status == BulkDeleteItemStatus.Deleted).And.HaveCount(size);
            counter.Commits.Should().Be(1, "all eligible records are removed in one owned transaction");
            selectsBySize[size] = counter.Selects;
            // Local SQLite in-memory evidence only; not a production-provider latency claim.
            _output.WriteLine($"files={size} selects={counter.Selects} commits={counter.Commits} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}");
        }

        selectsBySize.Values.Distinct().Should().ContainSingle(
            "file lookup, active-job count, and reference clearing are set-based rather than per file");
    }

    [Fact]
    public async Task DeleteFilesAsync_SaveChangesFailure_RollsBackAndKeepsStoredFiles()
    {
        GcodeFile first = await SeedFileAsync("one.gcode");
        GcodeFile second = await SeedFileAsync("two.gcode");
        Guid historicalJobId = await SeedJobAsync(first.Id, PrintJobStatus.Completed);
        (Guid directMappingId, _) = await SeedMappingsAsync(first.Id);

        IReadOnlyList<BulkDeleteItemResult> results = await DeleteAsync(
            [first.Id, second.Id], interceptor: new FailingSaveInterceptor());

        results.Should().Equal(
            new BulkDeleteItemResult(first.Id, BulkDeleteItemStatus.Failed, BulkDeleteItemReasons.DatabaseFailed),
            new BulkDeleteItemResult(second.Id, BulkDeleteItemStatus.Failed, BulkDeleteItemReasons.DatabaseFailed));
        await using AppDbContext db = NewDb();
        (await db.GcodeFiles.CountAsync()).Should().Be(2);
        (await db.PrintJobs.SingleAsync(j => j.Id == historicalJobId)).GcodeFileId.Should().Be(first.Id,
            "reference clearing is rolled back with the failed unit");
        (await db.PartOutputMappings.AnyAsync(m => m.Id == directMappingId))
            .Should().BeTrue("the ExecuteDelete of direct mappings is rolled back with the failed unit");
        (await db.PrintProjectFiles.AnyAsync(f => f.GcodeFileId == first.Id)).Should().BeTrue();
        File.Exists(StoredPath("one.gcode")).Should().BeTrue();
        File.Exists(StoredPath("two.gcode")).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteFilesAsync_CommitCallFailure_ReportsUnknownAndKeepsStoredFiles()
    {
        GcodeFile file = await SeedFileAsync("commit.gcode");

        IReadOnlyList<BulkDeleteItemResult> results = await DeleteAsync(
            [file.Id], interceptor: new FailingCommitInterceptor());

        results.Should().Equal(
            new BulkDeleteItemResult(file.Id, BulkDeleteItemStatus.Unknown, BulkDeleteItemReasons.CommitOutcomeUnknown));
        File.Exists(StoredPath("commit.gcode")).Should().BeTrue("stored files are only removed after a known commit");
    }

    [Fact]
    public async Task DeleteFilesAsync_LookupFailure_ReportsEveryIdFailedWithoutChanges()
    {
        GcodeFile file = await SeedFileAsync("lookup.gcode");
        Guid missing = Guid.NewGuid();

        IReadOnlyList<BulkDeleteItemResult> results = await DeleteAsync(
            [file.Id, missing, file.Id], interceptor: new FailingReaderInterceptor());

        results.Should().Equal(
            new BulkDeleteItemResult(file.Id, BulkDeleteItemStatus.Failed, BulkDeleteItemReasons.DatabaseFailed),
            new BulkDeleteItemResult(missing, BulkDeleteItemStatus.Failed, BulkDeleteItemReasons.DatabaseFailed));
        await using AppDbContext db = NewDb();
        (await db.GcodeFiles.AnyAsync(f => f.Id == file.Id)).Should().BeTrue();
        File.Exists(StoredPath("lookup.gcode")).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteFilesAsync_StoredFileCannotBeRemoved_ReportsCleanupIncomplete()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // Directory write permissions are not enforced for these principals.
        }

        string lockedDir = Path.Join(_storageRoot, "locked");
        _ = Directory.CreateDirectory(lockedDir);
        GcodeFile locked = await SeedFileAsync("locked.gcode", directory: "locked");
        GcodeFile free = await SeedFileAsync("free.gcode");
        File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        IReadOnlyList<BulkDeleteItemResult> results = await DeleteAsync([locked.Id, free.Id]);

        results.Should().Equal(
            new BulkDeleteItemResult(locked.Id, BulkDeleteItemStatus.DeletedCleanupIncomplete, BulkDeleteItemReasons.FileCleanupFailed),
            new BulkDeleteItemResult(free.Id, BulkDeleteItemStatus.Deleted));
        results[0].RecordRemoved.Should().BeTrue();
        await using AppDbContext db = NewDb();
        (await db.GcodeFiles.AnyAsync()).Should().BeFalse("both records were committed before cleanup ran");
        File.Exists(Path.Join(lockedDir, "locked.gcode")).Should().BeTrue();
    }

    private AppDbContext NewDb(IInterceptor? interceptor = null)
    {
        DbContextOptionsBuilder<AppDbContext> builder = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection);
        if (interceptor is not null)
        {
            _ = builder.AddInterceptors(interceptor);
        }

        return new AppDbContext(builder.Options);
    }

    private async Task<IReadOnlyList<BulkDeleteItemResult>> DeleteAsync(
        IEnumerable<Guid> ids,
        CommandCounter? counter = null,
        IInterceptor? interceptor = null)
    {
        await using AppDbContext db = NewDb(interceptor ?? counter);
        var uow = new AppUnitOfWork(db, Mock.Of<ISensitiveDataProtector>());
        var storagePath = new Mock<IStoragePathService>();
        _ = storagePath.Setup(x => x.GetGcodeStorageDirectory()).Returns(_storageRoot);
        var service = new GcodeFilesService(
            uow.GcodeFiles,
            uow,
            NullLogger<GcodeFilesService>.Instance,
            storagePath.Object,
            Mock.Of<IGcodeMetadataExtractorService>(),
            Mock.Of<IGcodeThumbnailExtractorService>(),
            Mock.Of<IFolderManagementService>(),
            Mock.Of<IStoredFileOperationsService>(),
            Mock.Of<IPrintFarmerTelemetryService>());
        if (counter is not null)
        {
            counter.Enabled = true;
        }

        return await service.DeleteFilesAsync(ids, CancellationToken.None);
    }

    private string StoredPath(string fileName, string? directory = null) =>
        directory is null ? Path.Join(_storageRoot, fileName) : Path.Join(_storageRoot, directory, fileName);

    private async Task<GcodeFile> SeedFileAsync(
        string fileName,
        bool withThumbnail = false,
        bool writeStoredFile = true,
        string? directory = null)
    {
        string? thumbnail = withThumbnail ? Path.ChangeExtension(fileName, ".png") : null;
        if (writeStoredFile)
        {
            await File.WriteAllTextAsync(StoredPath(fileName, directory), "G1 X1");
            if (thumbnail is not null)
            {
                await File.WriteAllBytesAsync(StoredPath(thumbnail, directory), [1, 2, 3]);
            }
        }

        var file = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = fileName,
            FileName = fileName,
            FilePath = directory is null ? "/" : "/" + directory,
            ThumbnailFileName = thumbnail,
            FileHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            FileSizeBytes = 5,
            FolderId = _folderId,
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Source = GcodeSource.Upload,
        };
        await using AppDbContext db = NewDb();
        _ = db.GcodeFiles.Add(file);
        _ = await db.SaveChangesAsync();
        return file;
    }

    private async Task<Guid> SeedJobAsync(Guid gcodeFileId, PrintJobStatus status)
    {
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "job",
            GcodeFileId = gcodeFileId,
            Status = status,
            QueuedAt = DateTime.UtcNow,
        };
        await using AppDbContext db = NewDb();
        _ = db.PrintJobs.Add(job);
        _ = await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<(Guid DirectMappingId, Guid ProjectMappingId)> SeedMappingsAsync(Guid gcodeFileId)
    {
        Guid projectId = Guid.NewGuid();
        Guid projectFileId = Guid.NewGuid();
        Guid partA = Guid.NewGuid();
        Guid partB = Guid.NewGuid();
        Guid direct = Guid.NewGuid();
        Guid viaProject = Guid.NewGuid();
        await using AppDbContext db = NewDb();
        _ = db.PrintProjects.Add(new PrintProject { Id = projectId, Name = "Project" });
        _ = db.PrintProjectFiles.Add(new PrintProjectFile { Id = projectFileId, PrintProjectId = projectId, GcodeFileId = gcodeFileId });
        _ = db.PartInventories.Add(new PartInventory { Id = partA, Sku = "SKU-" + partA.ToString("N").ToUpperInvariant(), Name = "A" });
        _ = db.PartInventories.Add(new PartInventory { Id = partB, Sku = "SKU-" + partB.ToString("N").ToUpperInvariant(), Name = "B" });
        _ = db.PartOutputMappings.Add(new PartOutputMapping
        {
            Id = direct,
            GcodeFileId = gcodeFileId,
            PartInventoryId = partA,
            Quantity = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = db.PartOutputMappings.Add(new PartOutputMapping
        {
            Id = viaProject,
            PrintProjectFileId = projectFileId,
            PartInventoryId = partB,
            Quantity = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = await db.SaveChangesAsync();
        return (direct, viaProject);
    }

    private static void RestoreWritable(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private sealed class CommandCounter : DbCommandInterceptor, IDbTransactionInterceptor
    {
        public bool Enabled { get; set; }

        public int Selects { get; private set; }

        public int Commits { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Selects++;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                Commits++;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("Simulated save failure");
    }

    private sealed class FailingCommitInterceptor : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated commit transport failure");
    }

    private sealed class FailingReaderInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated database outage");
    }
}
