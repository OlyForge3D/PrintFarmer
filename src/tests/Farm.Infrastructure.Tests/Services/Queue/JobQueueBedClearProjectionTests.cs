using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Tests.Builders;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Services.Queue;

public sealed class JobQueueBedClearProjectionTests
{
    private const string ProjectionOperationHash =
        "1db171168f8040a82e0f527120678708649fb0c5e3f2b621bb97babd8d684531";

    [Fact]
    public async Task GetJobAsync_CalibrationWithoutCommand_ProjectsNoneAndLineage()
    {
        await using ProjectionFixture fixture = await ProjectionFixture.CreateAsync(
            JobKind.FilamentCalibration);

        JobQueuePrintJobDto result = await fixture.ReadAsync();

        result.JobKind.Should().Be(JobKind.FilamentCalibration);
        result.CalibrationProjectId.Should().Be(fixture.Job.CalibrationProjectId);
        result.CalibrationAttemptId.Should().Be(fixture.Job.CalibrationAttemptId);
        result.CalibrationOrchestrationId.Should().Be(fixture.Job.CalibrationOrchestrationId);
        result.PinnedPrinterConfigRevision.Should().Be(fixture.Job.PinnedPrinterConfigRevision);
        result.BedClearState.Should().Be(BedClearState.None);
        result.BedClearCommandId.Should().BeNull();
        result.BedClearIdempotencyKeySha256.Should().BeNull();
        result.BedClearExpiresAtUtc.Should().BeNull();
        result.Revision.Should().Be(fixture.Job.Revision);
        result.DispatchStateRevision.Should().Be(fixture.DispatchState.Revision);
        result.RowVersion.Should().Be(Convert.ToBase64String(fixture.Job.RowVersion!));
        result.DispatchStateRowVersion.Should().Be(
            Convert.ToBase64String(fixture.DispatchState.RowVersion!));
    }

    [Fact]
    public async Task GetJobAsync_LiveExactCommand_ProjectsAcknowledgedWireContract()
    {
        await using ProjectionFixture fixture = await ProjectionFixture.CreateAsync(
            JobKind.FilamentCalibration);
        BedClearCommandRecord command = await fixture.AcknowledgeAsync();

        JobQueuePrintJobDto result = await fixture.ReadAsync();

        result.BedClearState.Should().Be(BedClearState.Acknowledged);
        result.BedClearCommandId.Should().Be(command.Id);
        result.BedClearIdempotencyKeySha256.Should().Be(ProjectionOperationHash);
        result.BedClearExpiresAtUtc.Should().Be(command.ExpiresAtUtc);

        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(result, options));
        JsonElement root = document.RootElement;
        root.GetProperty("calibrationProjectId").GetGuid()
            .Should().Be(fixture.Job.CalibrationProjectId.GetValueOrDefault());
        root.GetProperty("calibrationAttemptId").GetGuid()
            .Should().Be(fixture.Job.CalibrationAttemptId.GetValueOrDefault());
        root.GetProperty("calibrationOrchestrationId").GetGuid()
            .Should().Be(fixture.Job.CalibrationOrchestrationId.GetValueOrDefault());
        root.GetProperty("pinnedPrinterConfigRevision").GetInt64()
            .Should().Be(fixture.Job.PinnedPrinterConfigRevision);
        root.GetProperty("bedClearState").GetString().Should().Be("Acknowledged");
        root.GetProperty("bedClearCommandId").GetGuid().Should().Be(command.Id);
        root.GetProperty("bedClearIdempotencyKeySha256").GetString()
            .Should().Be(ProjectionOperationHash);
        root.GetProperty("bedClearExpiresAtUtc").GetDateTime()
            .Should().Be(command.ExpiresAtUtc);
        root.GetProperty("revision").GetInt64().Should().Be(fixture.Job.Revision);
        root.GetProperty("dispatchStateRevision").GetInt64()
            .Should().Be(fixture.DispatchState.Revision);
        string json = root.GetRawText();
        json.Should().NotContain(command.ActorSubject);
        json.Should().NotContain(command.IdempotencyKey);
        json.Should().NotContain(command.RequestSha256);
    }

    [Theory]
    [InlineData(BedClearCommandStatus.Claimed)]
    [InlineData(BedClearCommandStatus.Accepted)]
    [InlineData(BedClearCommandStatus.Unknown)]
    public async Task GetJobAsync_ConsumedCommandStatus_ProjectsConsumed(
        BedClearCommandStatus commandStatus)
    {
        await using ProjectionFixture fixture = await ProjectionFixture.CreateAsync(
            JobKind.FilamentCalibration);
        BedClearCommandRecord command = await fixture.AcknowledgeAsync();
        command.Status = commandStatus;
        command.DispatchAttemptId = Guid.NewGuid();
        fixture.Job.Status = PrintJobStatus.Starting;
        fixture.DispatchState.ActiveJobId = fixture.Job.Id;
        fixture.DispatchState.ActiveDispatchAttemptId = command.DispatchAttemptId;
        fixture.ClearAcknowledgement();
        await fixture.Db.SaveChangesAsync();

        JobQueuePrintJobDto result = await fixture.ReadAsync();

        result.BedClearState.Should().Be(BedClearState.Consumed);
        result.BedClearCommandId.Should().Be(command.Id);
        result.BedClearIdempotencyKeySha256.Should().Be(ProjectionOperationHash);
        result.BedClearExpiresAtUtc.Should().Be(command.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(InvalidationCase.Terminal)]
    [InlineData(InvalidationCase.Expired)]
    [InlineData(InvalidationCase.WrongJob)]
    [InlineData(InvalidationCase.Reordered)]
    [InlineData(InvalidationCase.Cancelled)]
    [InlineData(InvalidationCase.Reassigned)]
    [InlineData(InvalidationCase.ConfigurationDrift)]
    [InlineData(InvalidationCase.JobRevisionDrift)]
    public async Task GetJobAsync_InvalidatedExactCommand_ProjectsInvalidated(
        InvalidationCase invalidation)
    {
        await using ProjectionFixture fixture = await ProjectionFixture.CreateAsync(
            JobKind.FilamentCalibration);
        BedClearCommandRecord command = await fixture.AcknowledgeAsync();
        await fixture.ApplyInvalidationAsync(invalidation);

        JobQueuePrintJobDto result = await fixture.ReadAsync();

        result.BedClearState.Should().Be(BedClearState.Invalidated);
        result.BedClearCommandId.Should().Be(command.Id);
        result.BedClearIdempotencyKeySha256.Should().Be(ProjectionOperationHash);
        result.BedClearExpiresAtUtc.Should().Be(command.ExpiresAtUtc);
    }

    [Fact]
    public async Task GetJobAsync_DispatchStateChange_UpdatesRevisionTokensAndInvalidates()
    {
        await using ProjectionFixture fixture = await ProjectionFixture.CreateAsync(
            JobKind.FilamentCalibration);
        _ = await fixture.AcknowledgeAsync();
        JobQueuePrintJobDto acknowledged = await fixture.ReadAsync();
        fixture.DispatchState.QueueRevision++;
        await fixture.Db.SaveChangesAsync();

        JobQueuePrintJobDto changed = await fixture.ReadAsync();

        changed.BedClearState.Should().Be(BedClearState.Invalidated);
        changed.Revision.Should().Be(acknowledged.Revision);
        changed.RowVersion.Should().Be(acknowledged.RowVersion);
        changed.DispatchStateRevision.Should().NotBeNull();
        acknowledged.DispatchStateRevision.Should().NotBeNull();
        changed.DispatchStateRevision!.Value.Should()
            .BeGreaterThan(acknowledged.DispatchStateRevision!.Value);
        changed.DispatchStateRowVersion.Should()
            .NotBe(acknowledged.DispatchStateRowVersion);
        changed.DispatchStateRevision.Should().Be(fixture.DispatchState.Revision);
        changed.DispatchStateRowVersion.Should().Be(
            Convert.ToBase64String(fixture.DispatchState.RowVersion!));
    }

    [Fact]
    public async Task GetJobAsync_ConcurrentDispatchChange_ReturnsSingleRelationalSnapshot()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"printfarmer-job-projection-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={databasePath};Default Timeout=30";
        try
        {
            DbContextOptions<AppDbContext> options =
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connectionString)
                    .Options;
            await using var readDb = new AppDbContext(options);
            await readDb.Database.EnsureCreatedAsync();
            _ = await readDb.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

            Printer printer = new PrinterBuilder()
                .WithId(Guid.NewGuid())
                .WithName("Snapshot Printer")
                .Build();
            var manufacturer = new Manufacturer
            {
                Id = Guid.NewGuid(),
                Name = "Snapshot Manufacturer",
            };
            var model = new PrinterModel
            {
                Id = Guid.NewGuid(),
                ManufacturerId = manufacturer.Id,
                Manufacturer = manufacturer,
                Name = "Snapshot Model",
            };
            printer.ManufacturerId = manufacturer.Id;
            printer.ModelId = model.Id;
            printer.Model = model;
            printer.ConfigurationRevision = 31;
            var folder = new FolderNode
            {
                Id = Guid.NewGuid(),
                Path = "/",
                FolderType = "gcode",
            };
            PrintJob job = new PrintJobBuilder()
                .WithAssignedPrinter(printer)
                .AsAssigned()
                .Build();
            job.GcodeFile!.FolderId = folder.Id;
            job.GcodeFile.Folder = folder;
            job.JobKind = JobKind.FilamentCalibration;
            job.PinnedPrinterConfigRevision = printer.ConfigurationRevision;
            var dispatchState = new PrinterDispatchState
            {
                PrinterId = printer.Id,
                Printer = printer,
                QueueRevision = 41,
            };
            readDb.Manufacturers.Add(manufacturer);
            readDb.Set<FolderNode>().Add(folder);
            readDb.PrintJobs.Add(job);
            readDb.PrinterDispatchStates.Add(dispatchState);
            await readDb.SaveChangesAsync();

            DateTime now = DateTime.UtcNow;
            string idempotencyKey = $"snapshot-{Guid.NewGuid():N}";
            dispatchState.AcknowledgedJobId = job.Id;
            dispatchState.AcknowledgedAtUtc = now;
            dispatchState.AcknowledgedBySubject = "snapshot-subject";
            dispatchState.AcknowledgementIdempotencyKey = idempotencyKey;
            dispatchState.AcknowledgementExpiresAtUtc = now.AddMinutes(5);
            dispatchState.AcknowledgedJobRowVersion = job.RowVersion!.ToArray();
            dispatchState.AcknowledgedQueueRevision = dispatchState.QueueRevision;
            dispatchState.AcknowledgedPrinterConfigRevision = printer.ConfigurationRevision;
            readDb.BedClearCommandRecords.Add(new BedClearCommandRecord
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                JobId = job.Id,
                IdempotencyKey = idempotencyKey,
                RequestSha256 = "snapshot-request-hash",
                ActorSubject = "snapshot-subject",
                JobRowVersion = job.RowVersion!.ToArray(),
                DispatchStateRowVersion = dispatchState.RowVersion!.ToArray(),
                QueueRevision = dispatchState.QueueRevision,
                PrinterConfigRevision = printer.ConfigurationRevision,
                Status = BedClearCommandStatus.Pending,
                OutboxEventId = Guid.NewGuid(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                ExpiresAtUtc = now.AddMinutes(5),
            });
            await readDb.SaveChangesAsync();
            long acknowledgedDispatchRevision = dispatchState.Revision;
            readDb.ChangeTracker.Clear();

            int readCount = 0;
            var dataService = new Mock<IQueueDataService>(MockBehavior.Strict);
            dataService
                .Setup(candidate => candidate.GetPrintJobByIdAsync(
                    job.Id,
                    It.IsAny<CancellationToken>()))
                .Returns(async (Guid _, CancellationToken ct) =>
                {
                    readDb.Database.CurrentTransaction.Should().NotBeNull();
                    PrintJob snapshot = await readDb.PrintJobs
                        .AsNoTracking()
                        .Include(candidate => candidate.GcodeFile)
                        .Include(candidate => candidate.AssignedPrinter)
                        .SingleAsync(candidate => candidate.Id == job.Id, ct);
                    if (Interlocked.Increment(ref readCount) == 1)
                    {
                        await IncrementDispatchQueueRevisionAsync(
                            connectionString,
                            printer.Id,
                            ct);
                    }

                    return snapshot;
                });
            var service = new JobQueueService(
                Mock.Of<IQueueRepository>(),
                dataService.Object,
                NullLogger<JobQueueService>.Instance,
                db: readDb);

            JobQueuePrintJobDto? concurrentRead = await service.GetJobAsync(
                job.Id,
                CancellationToken.None);
            JobQueuePrintJobDto? settledRead = await service.GetJobAsync(
                job.Id,
                CancellationToken.None);

            concurrentRead.Should().NotBeNull();
            concurrentRead!.BedClearState.Should().Be(BedClearState.Acknowledged);
            concurrentRead.DispatchStateRevision.Should()
                .Be(acknowledgedDispatchRevision);
            settledRead.Should().NotBeNull();
            settledRead!.BedClearState.Should().Be(BedClearState.Invalidated);
            settledRead.DispatchStateRevision.Should()
                .BeGreaterThan(acknowledgedDispatchRevision);
        }
        finally
        {
            // Scope the pool clear to this test's own connection string instead of
            // calling the process-wide ClearAllPools(), which would disrupt other
            // tests' pooled SQLite connections running concurrently now that this
            // assembly is no longer fully serialized.
            using (var pooledConnection = new SqliteConnection(connectionString))
            {
                SqliteConnection.ClearPool(pooledConnection);
            }

            File.Delete(databasePath);
        }
    }

    [Theory]
    [InlineData(JobKind.Standard)]
    [InlineData(null)]
    public async Task GetJobAsync_StandardOrLegacyJob_LeavesBedClearContractNull(
        JobKind? jobKind)
    {
        await using ProjectionFixture fixture = await ProjectionFixture.CreateAsync(jobKind);

        JobQueuePrintJobDto result = await fixture.ReadAsync();

        result.JobKind.Should().Be(jobKind);
        result.BedClearState.Should().BeNull();
        result.BedClearCommandId.Should().BeNull();
        result.BedClearIdempotencyKeySha256.Should().BeNull();
        result.BedClearExpiresAtUtc.Should().BeNull();
    }

    [Fact]
    public void HashIdempotencyKey_IsCaseSensitiveLowerCaseSha256()
    {
        string upperCase = BedClearCommandCorrelation.HashIdempotencyKey(
            "Operation-Key");
        string lowerCase = BedClearCommandCorrelation.HashIdempotencyKey(
            "operation-key");

        upperCase.Should().Be(
            "2067020c26d9ea1325101a132957f73f004564fa98e917aada048f0955c83ea9");
        lowerCase.Should().Be(
            "f9a170739cf356f6c28eaa79041de03a0463cb196136bf24c74a5d4b165d5371");
        upperCase.Should().MatchRegex("^[0-9a-f]{64}$");
        upperCase.Should().NotBe(lowerCase);
    }

    public enum InvalidationCase
    {
        Terminal,
        Expired,
        WrongJob,
        Reordered,
        Cancelled,
        Reassigned,
        ConfigurationDrift,
        JobRevisionDrift,
    }

    private sealed class ProjectionFixture : IAsyncDisposable
    {
        private readonly Mock<IQueueDataService> dataService;
        private readonly JobQueueService service;

        private ProjectionFixture(
            AppDbContext db,
            Printer printer,
            PrintJob job,
            PrinterDispatchState dispatchState)
        {
            Db = db;
            Printer = printer;
            Job = job;
            DispatchState = dispatchState;
            dataService = new Mock<IQueueDataService>(MockBehavior.Strict);
            dataService
                .Setup(candidate => candidate.GetPrintJobByIdAsync(
                    job.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => job);
            service = new JobQueueService(
                Mock.Of<IQueueRepository>(),
                dataService.Object,
                NullLogger<JobQueueService>.Instance,
                db: db);
        }

        public AppDbContext Db { get; }

        public Printer Printer { get; }

        public PrintJob Job { get; }

        public PrinterDispatchState DispatchState { get; }

        public static async Task<ProjectionFixture> CreateAsync(JobKind? jobKind)
        {
            DbContextOptions<AppDbContext> options =
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options;
            var db = new AppDbContext(options);
            Printer printer = new PrinterBuilder()
                .WithId(Guid.NewGuid())
                .WithName("Contract Printer")
                .Build();
            printer.ConfigurationRevision = 17;
            PrintJob job = new PrintJobBuilder()
                .WithAssignedPrinter(printer)
                .AsAssigned()
                .Build();
            job.JobKind = jobKind;
            job.CalibrationProjectId = jobKind == JobKind.FilamentCalibration
                ? Guid.NewGuid()
                : null;
            job.CalibrationAttemptId = jobKind == JobKind.FilamentCalibration
                ? Guid.NewGuid()
                : null;
            job.CalibrationOrchestrationId = jobKind == JobKind.FilamentCalibration
                ? Guid.NewGuid()
                : null;
            job.PinnedPrinterConfigRevision = jobKind == JobKind.FilamentCalibration
                ? printer.ConfigurationRevision
                : null;
            var dispatchState = new PrinterDispatchState
            {
                PrinterId = printer.Id,
                Printer = printer,
                QueueRevision = 23,
            };
            db.PrintJobs.Add(job);
            db.PrinterDispatchStates.Add(dispatchState);
            await db.SaveChangesAsync();

            return new ProjectionFixture(db, printer, job, dispatchState);
        }

        public async Task<BedClearCommandRecord> AcknowledgeAsync()
        {
            DateTime now = DateTime.UtcNow;
            const string idempotencyKey = "projection-operation";
            DispatchState.AcknowledgedJobId = Job.Id;
            DispatchState.AcknowledgedAtUtc = now;
            DispatchState.AcknowledgedBySubject = "subject-not-for-wire";
            DispatchState.AcknowledgementIdempotencyKey = idempotencyKey;
            DispatchState.AcknowledgementExpiresAtUtc = now.AddMinutes(5);
            DispatchState.AcknowledgedJobRowVersion = Job.RowVersion!.ToArray();
            DispatchState.AcknowledgedQueueRevision = DispatchState.QueueRevision;
            DispatchState.AcknowledgedPrinterConfigRevision = Printer.ConfigurationRevision;
            var command = new BedClearCommandRecord
            {
                Id = Guid.NewGuid(),
                PrinterId = Printer.Id,
                JobId = Job.Id,
                IdempotencyKey = idempotencyKey,
                RequestSha256 = "request-hash-not-for-wire",
                ActorSubject = "subject-not-for-wire",
                JobRowVersion = Job.RowVersion!.ToArray(),
                DispatchStateRowVersion = DispatchState.RowVersion!.ToArray(),
                QueueRevision = DispatchState.QueueRevision,
                PrinterConfigRevision = Printer.ConfigurationRevision,
                Status = BedClearCommandStatus.Pending,
                OutboxEventId = Guid.NewGuid(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                ExpiresAtUtc = now.AddMinutes(5),
            };
            Db.BedClearCommandRecords.Add(command);
            await Db.SaveChangesAsync();

            return command;
        }

        public async Task ApplyInvalidationAsync(InvalidationCase invalidation)
        {
            BedClearCommandRecord command = await Db.BedClearCommandRecords.SingleAsync();
            switch (invalidation)
            {
                case InvalidationCase.Terminal:
                    command.Status = BedClearCommandStatus.Rejected;
                    break;
                case InvalidationCase.Expired:
                    command.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
                    DispatchState.AcknowledgementExpiresAtUtc = command.ExpiresAtUtc;
                    break;
                case InvalidationCase.WrongJob:
                    DispatchState.AcknowledgedJobId = Guid.NewGuid();
                    break;
                case InvalidationCase.Reordered:
                    PrintJob insertedAhead = new PrintJobBuilder()
                        .WithAssignedPrinter(Printer)
                        .WithPriority((int)PrintJobPriority.Urgent)
                        .WithQueuePosition(2)
                        .AsQueued()
                        .Build();
                    Db.PrintJobs.Add(insertedAhead);
                    DispatchState.QueueRevision++;
                    break;
                case InvalidationCase.Cancelled:
                    Job.Status = PrintJobStatus.Cancelled;
                    break;
                case InvalidationCase.Reassigned:
                    Printer reassignedPrinter = new PrinterBuilder()
                        .WithId(Guid.NewGuid())
                        .WithName("Reassigned Printer")
                        .Build();
                    Db.Printers.Add(reassignedPrinter);
                    command.PrinterId = reassignedPrinter.Id;
                    break;
                case InvalidationCase.ConfigurationDrift:
                    Printer.ConfigurationRevision++;
                    Db.Entry(Printer)
                        .Property(candidate => candidate.ConfigurationRevision)
                        .IsModified = true;
                    break;
                case InvalidationCase.JobRevisionDrift:
                    Job.Status = PrintJobStatus.Queued;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(invalidation),
                        invalidation,
                        "Unknown invalidation case.");
            }

            await Db.SaveChangesAsync();
        }

        public void ClearAcknowledgement()
        {
            DispatchState.AcknowledgedJobId = null;
            DispatchState.AcknowledgedAtUtc = null;
            DispatchState.AcknowledgedBySubject = null;
            DispatchState.AcknowledgementIdempotencyKey = null;
            DispatchState.AcknowledgementExpiresAtUtc = null;
            DispatchState.AcknowledgedJobRowVersion = null;
            DispatchState.AcknowledgedQueueRevision = null;
            DispatchState.AcknowledgedPrinterConfigRevision = null;
        }

        public async Task<JobQueuePrintJobDto> ReadAsync()
        {
            JobQueuePrintJobDto? result = await service.GetJobAsync(
                Job.Id,
                CancellationToken.None);

            result.Should().NotBeNull();
            return result!;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
        }

    }

    private static async Task IncrementDispatchQueueRevisionAsync(
        string connectionString,
        Guid printerId,
        CancellationToken ct)
    {
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;
        await using var writerDb = new AppDbContext(options);
        PrinterDispatchState state = await writerDb.PrinterDispatchStates
            .SingleAsync(candidate => candidate.PrinterId == printerId, ct);
        state.QueueRevision++;
        await writerDb.SaveChangesAsync(ct);
    }
}
