using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Modules.Gcode.Services.Gcode;
using Farm.Modules.Gcode.Services.Gcode.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.Gcode.Tests.Services.Gcode;

public sealed class PrinterFileQueueServiceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ConfirmThenAdmit_ReplaysOnePinnedJobWithExistingCopiesSemanticsAsync(int copies)
    {
        await using Fixture test = await Fixture.CreateAsync(copies: copies);
        var service = test.CreateService();
        PrinterFileCopyOperation? waiting = await service.ConfirmAsync(test.Actor, test.OperationId, test.Confirmation(), default);
        Assert.Equal(PrinterFileOperationState.LocalReady, waiting!.State);
        Assert.Empty(await test.Db.PrintJobs.ToListAsync());

        PrinterFileCopyOperation? first = await service.AdmitAsync(test.Actor, test.OperationId, default);
        PrinterFileCopyOperation? replay = await test.CreateService().AdmitAsync(test.Actor, test.OperationId, default);
        PrinterFileCopyOperation? confirmReplay = await service.ConfirmAsync(test.Actor, test.OperationId, test.Confirmation(), default);

        Assert.True(first!.State == PrinterFileOperationState.Queued, first.ReasonCode);
        PrintJob job = await test.Db.PrintJobs.SingleAsync();
        Assert.Equal(PrinterFileOperationState.Queued, first!.State);
        Assert.Equal(first.JobIds, replay!.JobIds);
        Assert.Equal(first.JobIds, confirmReplay!.JobIds);
        Assert.Equal(job.Id, Assert.Single(first.JobIds!));
        Assert.Equal(test.AssetId, job.GcodeFileId);
        Assert.Equal(test.Digest, job.GcodeContentSha256);
        Assert.Equal(test.Bytes.Length, job.PinnedGcodeFileSizeBytes);
        Assert.Equal(test.DestinationId, job.AssignedPrinterId);
        Assert.Equal(test.Actor.ToString(), job.CreatorSubject);
        Assert.Equal(copies, job.Copies);
        Assert.Single(await test.Db.PrinterFileOperationJobs.ToListAsync());
        Assert.Single(await test.Db.QueueDispatchOutbox.ToListAsync());
        Assert.Equal(test.Bytes, await File.ReadAllBytesAsync(test.Path));
    }

    [Fact]
    public async Task UnconfirmedOrDeclined_NeverAdmitsAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        var service = test.CreateService();
        await Assert.ThrowsAsync<PrinterFileCommandException>(() => service.AdmitAsync(test.Actor, test.OperationId, default));
        PrinterFileCopyOperation? declined = await service.ConfirmAsync(test.Actor, test.OperationId,
            test.Confirmation() with { Accept = false }, default);
        Assert.Equal(PrinterFileOperationState.Cancelled, declined!.State);
        await Assert.ThrowsAsync<PrinterFileCommandException>(() => service.AdmitAsync(test.Actor, test.OperationId, default));
        Assert.Empty(await test.Db.PrintJobs.ToListAsync());
        Assert.True(File.Exists(test.Path));
    }

    [Theory]
    [InlineData("asset")]
    [InlineData("digest")]
    [InlineData("size")]
    [InlineData("payload")]
    public async Task ChangedConfirmationBinding_IsRejectedWithoutDecisionAsync(string field)
    {
        await using Fixture test = await Fixture.CreateAsync();
        PrinterFileSnapshotConfirmation confirmation = test.Confirmation();
        confirmation = field switch
        {
            "asset" => confirmation with { GcodeFileId = Guid.NewGuid() },
            "digest" => confirmation with { ContentSha256 = new string('0', 64) },
            "size" => confirmation with { SizeBytes = confirmation.SizeBytes + 1 },
            _ => confirmation with { PayloadSha256 = "changed" },
        };
        var error = await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            test.CreateService().ConfirmAsync(test.Actor, test.OperationId, confirmation, default));
        Assert.Equal("snapshot_confirmation_mismatch", error.Reason);
        Assert.Null((await test.Db.PrinterFileOperations.SingleAsync()).SnapshotConfirmedAtUtc);
        Assert.Empty(await test.Db.PrintJobs.ToListAsync());
    }

    [Theory]
    [InlineData("G28\nG1 X999 Y1\nM104 S0\nM140 S0\nM84\n", "gcode_motion_outside_build_volume")]
    [InlineData("G28\nRUN_SHELL_COMMAND CMD=bad\nM104 S0\nM140 S0\nM84\n", "gcode_contains_host_command")]
    [InlineData("G28\nG1 X1 Y1\n", "gcode_missing_final_reset")]
    public async Task Queue_RejectsUnsafeProgramAgainstActualDestinationAsync(string content, string reason)
    {
        await using Fixture test = await Fixture.CreateAsync(content);
        var service = test.CreateService();
        await service.ConfirmAsync(test.Actor, test.OperationId, test.Confirmation(), default);
        PrinterFileCopyOperation? blocked = await service.AdmitAsync(test.Actor, test.OperationId, default);
        Assert.Equal(PrinterFileOperationState.Blocked, blocked!.State);
        Assert.Equal(reason, blocked.ReasonCode);
        Assert.Empty(await test.Db.PrintJobs.ToListAsync());
        Assert.Empty(await test.Db.PrinterFileOperationJobs.ToListAsync());
        Assert.True(File.Exists(test.Path));
    }

    [Fact]
    public async Task LocalTamperingAfterConfirmation_NeverCreatesJobAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        var service = test.CreateService();
        await service.ConfirmAsync(test.Actor, test.OperationId, test.Confirmation(), default);
        await File.WriteAllTextAsync(test.Path, "G28\nG1 X999\n");
        PrinterFileCopyOperation? blocked = await service.AdmitAsync(test.Actor, test.OperationId, default);
        Assert.Equal(PrinterFileOperationState.Blocked, blocked!.State);
        Assert.Contains(blocked.ReasonCode, new[] { "gcode_size_mismatch", "gcode_byte_hash_mismatch" });
        Assert.Empty(await test.Db.PrintJobs.ToListAsync());
    }

    [Fact]
    public async Task RevokedActorOrIncompatibleDestination_NeverCreatesJobAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        var service = test.CreateService();
        await service.ConfirmAsync(test.Actor, test.OperationId, test.Confirmation(), default);
        test.Allowed = false;
        Assert.Null(await service.AdmitAsync(test.Actor, test.OperationId, default));
        test.Allowed = true;
        test.Compatible = false;
        PrinterFileCopyOperation? blocked = await service.AdmitAsync(test.Actor, test.OperationId, default);
        Assert.Equal("destination_incompatible", blocked!.ReasonCode);
        Assert.Empty(await test.Db.PrintJobs.ToListAsync());
        test.Compatible = true;
        PrinterFileCopyOperation? retried = await service.AdmitAsync(test.Actor, test.OperationId, default);
        Assert.Equal(PrinterFileOperationState.Queued, retried!.State);
        Assert.Single(await test.Db.PrintJobs.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentIndependentAdmissions_ConvergeOnOnePinnedJobAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        await test.CreateService().ConfirmAsync(test.Actor, test.OperationId, test.Confirmation(), default);
        await using AppDbContext firstDb = test.CreateContext();
        await using AppDbContext secondDb = test.CreateContext();
        var firstService = test.CreateService(context: firstDb);
        var secondService = test.CreateService(context: secondDb);
        PrinterFileCopyOperation?[] results = await Task.WhenAll(
            Task.Run(() => firstService.AdmitAsync(test.Actor, test.OperationId, default)),
            Task.Run(() => secondService.AdmitAsync(test.Actor, test.OperationId, default)));
        Assert.All(results, result => Assert.Equal(PrinterFileOperationState.Queued, result!.State));
        Assert.Equal(results[0]!.JobIds, results[1]!.JobIds);
        test.Db.ChangeTracker.Clear();
        Assert.Single(await test.Db.PrintJobs.ToArrayAsync());
        Assert.Single(await test.Db.PrinterFileOperationJobs.ToArrayAsync());
        Assert.Single(await test.Db.QueueDispatchOutbox.ToArrayAsync());
    }

    [Fact]
    public async Task ConcurrentContentBoundConfirmations_PersistOneDecisionAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        await using AppDbContext firstDb = test.CreateContext();
        await using AppDbContext secondDb = test.CreateContext();
        PrinterFileCopyOperation?[] results = await Task.WhenAll(
            Task.Run(() => test.CreateService(context: firstDb).ConfirmAsync(test.Actor, test.OperationId, test.Confirmation(), default)),
            Task.Run(() => test.CreateService(context: secondDb).ConfirmAsync(test.Actor, test.OperationId, test.Confirmation(), default)));
        Assert.All(results, result => Assert.Equal(PrinterFileOperationState.LocalReady, result!.State));
        test.Db.ChangeTracker.Clear();
        PrinterFileOperation operation = await test.Db.PrinterFileOperations.SingleAsync();
        Assert.NotNull(operation.SnapshotConfirmationSha256);
        Assert.NotNull(operation.SnapshotConfirmedAtUtc);
        Assert.Empty(await test.Db.PrintJobs.ToArrayAsync());
    }

    [Fact]
    public async Task CancellationBeforeAdmissionAndAfterCommit_RespectDurableJobBoundaryAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        await test.CreateService().ConfirmAsync(test.Actor, test.OperationId, test.Confirmation(), default);
        await using AppDbContext cancelDb = test.CreateContext();
        var access = new Mock<IPrinterFileAccessService>();
        access.Setup(x => x.CanReadAsync(test.Actor, It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        access.Setup(x => x.CanActAsync(test.Actor, It.IsAny<Guid>(), PrinterFileAction.Queue,
            test.DestinationId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        access.Setup(x => x.CanAccessLocalAsync(test.Actor, test.AssetId,
            PrinterGroupAccessLevel.Submit, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var cancel = new PrinterFileCopyService(cancelDb, access.Object, Mock.Of<ISettingsService>(), Mock.Of<IStoragePathService>());
        PrinterFileCopyOperation? cancelled = await cancel.CancelAsync(test.Actor, test.OperationId);
        Assert.Equal(PrinterFileOperationState.Cancelled, cancelled!.State);
        test.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            test.CreateService().AdmitAsync(test.Actor, test.OperationId, default));
        Assert.Empty(await test.Db.PrintJobs.ToArrayAsync());

        await using Fixture committed = await Fixture.CreateAsync();
        await committed.CreateService().ConfirmAsync(committed.Actor, committed.OperationId, committed.Confirmation(), default);
        PrinterFileCopyOperation? queued = await committed.CreateService().AdmitAsync(committed.Actor, committed.OperationId, default);
        var committedAccess = new Mock<IPrinterFileAccessService>();
        committedAccess.Setup(x => x.CanReadAsync(committed.Actor, It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        committedAccess.Setup(x => x.CanActAsync(committed.Actor, It.IsAny<Guid>(), PrinterFileAction.Queue,
            committed.DestinationId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        committedAccess.Setup(x => x.CanAccessLocalAsync(committed.Actor, committed.AssetId,
            PrinterGroupAccessLevel.Submit, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var lateCancel = new PrinterFileCopyService(committed.Db, committedAccess.Object,
            Mock.Of<ISettingsService>(), Mock.Of<IStoragePathService>());
        PrinterFileCopyOperation? kept = await lateCancel.CancelAsync(committed.Actor, committed.OperationId);
        Assert.Equal(PrinterFileOperationState.Queued, kept!.State);
        Assert.Equal(queued!.JobIds, kept.JobIds);
        Assert.Single(await committed.Db.PrintJobs.ToArrayAsync());
    }

    [Fact]
    public async Task CrashAfterJobSaveBeforeOuterCommit_RollsBackJobLinkAndOutboxThenConvergesAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        var service = test.CreateService();
        await service.ConfirmAsync(test.Actor, test.OperationId, test.Confirmation(), default);
        var failing = test.CreateService(crashAfterSave: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.AdmitAsync(test.Actor, test.OperationId, default));
        test.Db.ChangeTracker.Clear();
        Assert.Empty(await test.Db.PrintJobs.ToListAsync());
        Assert.Empty(await test.Db.PrinterFileOperationJobs.ToListAsync());
        Assert.Empty(await test.Db.QueueDispatchOutbox.ToListAsync());
        Assert.Equal(PrinterFileOperationState.LocalReady, (await test.Db.PrinterFileOperations.SingleAsync()).State);
        PrinterFileCopyOperation? retried = await test.CreateService().AdmitAsync(test.Actor, test.OperationId, default);
        Assert.Equal(PrinterFileOperationState.Queued, retried!.State);
        Assert.Single(await test.Db.PrintJobs.ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = System.IO.Path.Join(System.IO.Path.GetTempPath(), "catalog-queue", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Join(Root, "part.gcode");
        public Guid Actor { get; } = Guid.NewGuid();
        public Guid AssetId { get; } = Guid.NewGuid();
        public Guid DestinationId { get; } = Guid.NewGuid();
        public Guid OperationId { get; } = Guid.NewGuid();
        public byte[] Bytes { get; private set; } = [];
        public string Digest => Convert.ToHexString(SHA256.HashData(Bytes));
        public AppDbContext Db { get; private set; } = null!;
        public bool Allowed { get; set; } = true;
        public bool Compatible { get; set; } = true;

        public static async Task<Fixture> CreateAsync(
            string content = "G28\nG1 X1 Y1 F3000\nM104 S0\nM140 S0\nM84\n", int copies = 1)
        {
            Fixture test = new();
            Directory.CreateDirectory(test.Root);
            test.Bytes = Encoding.UTF8.GetBytes(content);
            await File.WriteAllBytesAsync(test.Path, test.Bytes);
            test.Db = new(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={System.IO.Path.Join(test.Root, "queue.db")}").Options);
            await test.Db.Database.EnsureCreatedAsync();
            Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = "maker" };
            PrinterModel model = new() { Id = Guid.NewGuid(), Name = "model", Manufacturer = manufacturer };
            Printer printer = new()
            {
                Id = test.DestinationId,
                Name = "destination",
                Model = model,
                Manufacturer = manufacturer,
                IsEnabled = true,
                MaxBuildVolumeX = 200,
                MaxBuildVolumeY = 200,
                MaxBuildVolumeZ = 200,
            };
            FolderNode folder = new() { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" };
            GcodeFile file = new()
            {
                Id = test.AssetId,
                Folder = folder,
                FileName = "part.gcode",
                Name = "part.gcode",
                FilePath = "/",
                FileHash = test.Digest,
                FileSizeBytes = test.Bytes.Length,
            };
            PrinterFileLocation location = new()
            {
                Id = Guid.NewGuid(),
                SourcePrinterId = printer.Id,
                RegisteredPrinterId = printer.Id,
                StorageRoot = "gcodes",
                RelativePath = "part.gcode",
                IdentityKey = "source",
            };
            var command = new PrinterFileCopyCommand(location.Id, 1, "/", PrinterFileAction.Queue,
                printer.Id, new(Copies: copies));
            var snapshot = new
            {
                Command = command,
                StorageRoot = "gcodes",
                RelativePath = "part.gcode",
                SizeBytes = (long?)test.Bytes.Length,
                ModifiedValue = (string?)null,
                ProviderVersion = (string?)null,
                ProviderDigest = (string?)null,
            };
            test.Db.AddRange(printer, file, location, new PrinterFileOperation
            {
                Id = test.OperationId,
                ActorId = test.Actor,
                CommandKeyHash = "command",
                PayloadSha256 = "payload",
                LocationId = location.Id,
                SourcePrinterId = printer.Id,
                DestinationPrinterId = printer.Id,
                Action = PrinterFileAction.Queue,
                State = PrinterFileOperationState.AwaitingSourceConfirmation,
                GcodeFileId = file.Id,
                ContentSha256 = test.Digest,
                SizeBytes = test.Bytes.Length,
                Verification = PrinterFileVerification.UnverifiedSnapshot,
                CommandJson = JsonSerializer.Serialize(snapshot),
            });
            await test.Db.SaveChangesAsync();
            test.Db.ChangeTracker.Clear();
            return test;
        }

        public PrinterFileSnapshotConfirmation Confirmation() => new(AssetId, Digest, Bytes.Length, "payload", true);

        public AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={System.IO.Path.Join(Root, "queue.db")}").Options);

        public PrinterFileQueueService CreateService(bool crashAfterSave = false, AppDbContext? context = null)
        {
            AppDbContext db = context ?? Db;
            var access = new Mock<IPrinterFileAccessService>();
            access.Setup(x => x.CanActAsync(Actor, It.IsAny<Guid>(), PrinterFileAction.Queue,
                    DestinationId, It.IsAny<CancellationToken>())).ReturnsAsync(() => Allowed);
            access.Setup(x => x.CanAccessLocalAsync(Actor, AssetId,
                    PrinterGroupAccessLevel.Submit, It.IsAny<CancellationToken>())).ReturnsAsync(() => Allowed);
            var files = new Mock<IStoredFileOperationsService>();
            files.Setup(x => x.GetFullFilePath(It.IsAny<StoredFile>())).Returns(Path);
            var printers = new Mock<IPrintersRepository>();
            printers.Setup(x => x.FindByIdWithIncludesAsync(DestinationId, It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken ct) => db.Printers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct));
            var targets = new Mock<IPrinterFileTargetValidator>();
            targets.Setup(x => x.CanQueueAsync(It.IsAny<GcodeFile>(), DestinationId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Compatible);
            var data = new Mock<IQueueDataService>();
            data.Setup(x => x.GetGcodeFileAsync(AssetId, It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken ct) => db.GcodeFiles.SingleOrDefaultAsync(x => x.Id == id, ct));
            data.Setup(x => x.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken ct) => db.Printers.ToListAsync(ct));
            JobQueueService queue = new(new EfQueueRepository(db), data.Object,
                NullLogger<JobQueueService>.Instance, db: db,
                sequenceAllocator: new DbOutboxSequenceAllocator(), positionAllocator: new QueuePositionAllocator(db));
            IPrinterFileQueueAdmission writer = crashAfterSave ? new CrashAfterSave(queue) : queue;
            return new(db, access.Object, files.Object, printers.Object,
                new GcodeSafetyValidator(), targets.Object, writer);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class CrashAfterSave(IPrinterFileQueueAdmission inner) : IPrinterFileQueueAdmission
    {
        public async Task<JobQueuePrintJobDto?> AddAsync(PrinterFileOperation operation, QueuePrintJobDto request, CancellationToken ct)
        {
            _ = await inner.AddAsync(operation, request, ct);
            throw new InvalidOperationException("simulated process failure before operation transaction commit");
        }

        public Task NotifyCommittedAsync(Guid printerId, CancellationToken ct) =>
            throw new InvalidOperationException("must not notify before commit");
    }
}
