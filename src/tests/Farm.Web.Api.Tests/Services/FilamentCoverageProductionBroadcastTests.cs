using Farm.Api.Services.PrintQueue;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Services.StorageManagement;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class FilamentCoverageProductionBroadcastTests
{
    [Fact]
    public async Task QueueCopyChange_BroadcastsAfterPersistence()
    {
        Guid printerId = Guid.NewGuid();
        PrintJob job = Job(printerId);
        Mock<IPrintJobManagementRepository> repository = new(MockBehavior.Strict);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        MockSequence sequence = new();
        repository.InSequence(sequence)
            .Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        repository.InSequence(sequence)
            .Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        broadcaster.InSequence(sequence)
            .Setup(b => b.BroadcastPrinterChangedAsync(
                printerId,
                FilamentCoverageChangeReasons.QueueChanged,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        PrintJobManagementService service = QueueService(repository.Object, broadcaster.Object);

        _ = await service.UpdateJobDetailsAsync(
            job.Id.ToString(),
            new UpdateJobDetailsRequest { Copies = 3 });

        job.Copies.Should().Be(3);
        broadcaster.VerifyAll();
    }

    [Fact]
    public async Task QueueUnrelatedDetailsChange_DoesNotBroadcastCoverage()
    {
        Guid printerId = Guid.NewGuid();
        PrintJob job = Job(printerId);
        Mock<IPrintJobManagementRepository> repository = new(MockBehavior.Strict);
        repository.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        PrintJobManagementService service = QueueService(repository.Object, broadcaster.Object);

        _ = await service.UpdateJobDetailsAsync(
            job.Id.ToString(),
            new UpdateJobDetailsRequest { Name = "renamed" });

        broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task QueueRequiredMaterialChange_BroadcastsCoverage()
    {
        Guid printerId = Guid.NewGuid();
        PrintJob job = Job(printerId);
        job.RequiredMaterialType = "PLA";
        Mock<IPrintJobManagementRepository> repository = new(MockBehavior.Strict);
        repository.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        broadcaster
            .Setup(b => b.BroadcastPrinterChangedAsync(
                printerId,
                FilamentCoverageChangeReasons.QueueChanged,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        PrintJobManagementService service = QueueService(repository.Object, broadcaster.Object);

        _ = await service.UpdateJobDetailsAsync(
            job.Id.ToString(),
            new UpdateJobDetailsRequest { RequiredMaterialType = "PETG" });

        broadcaster.VerifyAll();
    }

    [Fact]
    public async Task QueueCopyChange_WhenPersistenceFails_DoesNotBroadcast()
    {
        PrintJob job = Job(Guid.NewGuid());
        Mock<IPrintJobManagementRepository> repository = new(MockBehavior.Strict);
        repository.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("save failed"));
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        PrintJobManagementService service = QueueService(repository.Object, broadcaster.Object);

        Func<Task> act = () => service.UpdateJobDetailsAsync(
            job.Id.ToString(),
            new UpdateJobDetailsRequest { Copies = 2 });

        await act.Should().ThrowAsync<DbUpdateException>();
        broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task QueueAssignmentMove_BroadcastsOldAndNewPrintersAfterPersistence()
    {
        Guid oldPrinterId = Guid.NewGuid();
        Guid newPrinterId = Guid.NewGuid();
        PrintJob job = Job(oldPrinterId);
        Mock<IPrintJobManagementRepository> repository = new(MockBehavior.Strict);
        repository.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repository.Setup(r => r.UpdateAsync(job, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        broadcaster
            .Setup(b => b.BroadcastPrinterChangedAsync(
                It.IsAny<Guid>(),
                FilamentCoverageChangeReasons.JobAssignment,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        PrintJobManagementService service = QueueService(repository.Object, broadcaster.Object);

        _ = await service.UpdateJobAsync(
            job.Id.ToString(),
            new UpdateQueueJobRequest { AssignedPrinterId = newPrinterId.ToString() },
            "user");

        broadcaster.Verify(
            b => b.BroadcastPrinterChangedAsync(
                oldPrinterId,
                FilamentCoverageChangeReasons.JobAssignment,
                It.IsAny<CancellationToken>()),
            Times.Once);
        broadcaster.Verify(
            b => b.BroadcastPrinterChangedAsync(
                newPrinterId,
                FilamentCoverageChangeReasons.JobAssignment,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CancelJobAsync_AssignedJob_BroadcastsQueueChangedAfterPersistence()
    {
        PrintJob job = Job(Guid.NewGuid());
        Mock<IPrintJobManagementRepository> repository = new(MockBehavior.Strict);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        MockSequence sequence = new();
        repository.InSequence(sequence)
            .Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        repository.InSequence(sequence)
            .Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        broadcaster.InSequence(sequence)
            .Setup(b => b.BroadcastPrinterChangedAsync(
                job.AssignedPrinterId!.Value,
                FilamentCoverageChangeReasons.QueueChanged,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        PrintJobManagementService service = QueueService(repository.Object, broadcaster.Object);

        await service.CancelJobAsync(job.Id.ToString(), "user");

        broadcaster.VerifyAll();
    }

    [Fact]
    public async Task CancelJobAsync_PersistenceFails_DoesNotBroadcast()
    {
        PrintJob job = Job(Guid.NewGuid());
        Mock<IPrintJobManagementRepository> repository = new(MockBehavior.Strict);
        repository.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("save failed"));
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        PrintJobManagementService service = QueueService(repository.Object, broadcaster.Object);

        Func<Task> act = () => service.CancelJobAsync(job.Id.ToString(), "user");

        await act.Should().ThrowAsync<DbUpdateException>();
        broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancelJobAsync_UnassignedJob_DoesNotBroadcast()
    {
        PrintJob job = Job(Guid.NewGuid());
        job.AssignedPrinterId = null;
        Mock<IPrintJobManagementRepository> repository = new(MockBehavior.Strict);
        repository.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        PrintJobManagementService service = QueueService(repository.Object, broadcaster.Object);

        await service.CancelJobAsync(job.Id.ToString(), "user");

        broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AbortPrintAsync_AssignedActiveJob_BroadcastsQueueChangedAfterPersistence()
    {
        PrintJob job = Job(Guid.NewGuid());
        job.Status = PrintJobStatus.Printing;
        Mock<IPrintJobManagementRepository> repository = new(MockBehavior.Strict);
        repository.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Mock<IPrintersService> printers = new(MockBehavior.Strict);
        printers.Setup(p => p.CancelPrintAsync(job.AssignedPrinterId!.Value, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        broadcaster.Setup(b => b.BroadcastPrinterChangedAsync(
                job.AssignedPrinterId!.Value,
                FilamentCoverageChangeReasons.QueueChanged,
                It.IsAny<CancellationToken>()))
            .Callback(() => job.Status.Should().Be(PrintJobStatus.Queued))
            .Returns(Task.CompletedTask);
        PrintJobManagementService service = QueueService(repository.Object, broadcaster.Object, printers.Object);

        await service.AbortPrintAsync(job.Id.ToString(), "user");

        broadcaster.VerifyAll();
    }

    [Fact]
    public async Task RerunJobAsync_AssignedOriginal_BroadcastsJobAssignmentAfterAddSucceeds()
    {
        PrintJob original = Job(Guid.NewGuid());
        original.Status = PrintJobStatus.Completed;
        Mock<IPrintJobManagementRepository> repository = new(MockBehavior.Strict);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        MockSequence sequence = new();
        repository.InSequence(sequence)
            .Setup(r => r.GetByIdAsync(original.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(original);
        repository.InSequence(sequence)
            .Setup(r => r.GetMaxQueuePositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(4);
        repository.InSequence(sequence)
            .Setup(r => r.AddAsync(It.IsAny<PrintJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob job, CancellationToken _) => job);
        broadcaster.InSequence(sequence)
            .Setup(b => b.BroadcastPrinterChangedAsync(
                original.AssignedPrinterId!.Value,
                FilamentCoverageChangeReasons.JobAssignment,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        PrintJobManagementService service = QueueService(repository.Object, broadcaster.Object);

        _ = await service.RerunJobAsync(original.Id.ToString(), "user");

        broadcaster.VerifyAll();
    }

    [Fact]
    public async Task RerunJobAsync_UnassignedOriginal_DoesNotBroadcast()
    {
        PrintJob original = Job(Guid.NewGuid());
        original.AssignedPrinterId = null;
        original.Status = PrintJobStatus.Completed;
        Mock<IPrintJobManagementRepository> repository = new(MockBehavior.Strict);
        repository.Setup(r => r.GetByIdAsync(original.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(original);
        repository.Setup(r => r.GetMaxQueuePositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(4);
        repository.Setup(r => r.AddAsync(It.IsAny<PrintJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob job, CancellationToken _) => job);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        PrintJobManagementService service = QueueService(repository.Object, broadcaster.Object);

        _ = await service.RerunJobAsync(original.Id.ToString(), "user");

        broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BulkReorderJobsAsync_ReorderOnly_DoesNotBroadcast()
    {
        PrintJob job = Job(Guid.NewGuid());
        Mock<IPrintJobManagementRepository> repository = new(MockBehavior.Strict);
        repository.Setup(r => r.GetByIdAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        PrintJobManagementService service = QueueService(repository.Object, broadcaster.Object);

        QueueBulkOperationResultDto result = await service.BulkReorderJobsAsync(
            [new QueueJobReorderMove { JobId = job.Id.ToString(), NewPosition = 9 }],
            "user");

        result.SuccessfulCount.Should().Be(1);
        broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Completion_BroadcastsQueueAndConsumedSpoolWeightAfterPersistence()
    {
        string databaseName = Guid.NewGuid().ToString();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        Guid printerId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        await using (AppDbContext seed = new(options))
        {
            Printer printer = new()
            {
                Id = printerId,
                Name = "completion",
                ServerUrl = "http://printer.local",
                Backend = (int)PrinterBackend.OctoPrint,
                CurrentSpoolId = 5,
            };
            seed.Printers.Add(printer);
            seed.PrintJobs.Add(new PrintJob
            {
                Id = jobId,
                Name = "job",
                AssignedPrinterId = printerId,
                AssignedPrinter = printer,
                Status = PrintJobStatus.Printing,
                Copies = 1,
                EstimatedFilamentUsage = 10,
                ActualStartTime = DateTime.UtcNow.AddMinutes(-5),
                QueuedAt = DateTime.UtcNow.AddMinutes(-10),
            });
            _ = await seed.SaveChangesAsync();
        }

        Mock<IBackendClientFactory> backendFactory = new();
        backendFactory.Setup(f => f.GetClient((int)PrinterBackend.OctoPrint))
            .Returns(new Mock<IBackendClient>().Object);
        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(s => s.ConsumeFilamentAsync(5, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        broadcaster
            .Setup(b => b.BroadcastPrinterChangedAsync(
                printerId,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((_, _, _) =>
            {
                using AppDbContext verify = new(options);
                verify.PrintJobs.Single(j => j.Id == jobId).Status.Should().Be(PrintJobStatus.Completed);
            })
            .Returns(Task.CompletedTask);
        await using AppDbContext db = new(options);
        PrintJobCompletionService service = new(
            db,
            Hub(),
            NullLogger<PrintJobCompletionService>.Instance,
            backendFactory: backendFactory.Object,
            spoolmanService: spoolman.Object,
            coverageBroadcaster: broadcaster.Object);

        bool completed = await service.MarkCurrentJobAsCompletedAsync(
            printerId,
            "complete",
            new PrinterTerminalObservation(null),
            CancellationToken.None);

        completed.Should().BeTrue();
        broadcaster.Verify(
            b => b.BroadcastPrinterChangedAsync(
                printerId,
                FilamentCoverageChangeReasons.QueueChanged,
                It.IsAny<CancellationToken>()),
            Times.Once);
        broadcaster.Verify(
            b => b.BroadcastPrinterChangedAsync(
                printerId,
                FilamentCoverageChangeReasons.SpoolWeight,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Failure_BroadcastsQueueAndConsumedSpoolWeightAfterPersistence()
    {
        string databaseName = Guid.NewGuid().ToString();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        Guid printerId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        await using (AppDbContext seed = new(options))
        {
            Printer printer = new()
            {
                Id = printerId,
                Name = "failure",
                ServerUrl = "http://printer.local",
                Backend = (int)PrinterBackend.OctoPrint,
                CurrentSpoolId = 5,
            };
            seed.Printers.Add(printer);
            seed.PrintJobs.Add(new PrintJob
            {
                Id = jobId,
                Name = "job",
                AssignedPrinterId = printerId,
                AssignedPrinter = printer,
                Status = PrintJobStatus.Printing,
                Copies = 1,
                EstimatedFilamentUsage = 10,
                ActualStartTime = DateTime.UtcNow.AddMinutes(-5),
                QueuedAt = DateTime.UtcNow.AddMinutes(-10),
            });
            _ = await seed.SaveChangesAsync();
        }

        Mock<IBackendClientFactory> backendFactory = new();
        backendFactory.Setup(f => f.GetClient((int)PrinterBackend.OctoPrint))
            .Returns(new Mock<IBackendClient>().Object);
        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(s => s.ConsumeFilamentAsync(5, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        broadcaster
            .Setup(b => b.BroadcastPrinterChangedAsync(
                printerId,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((_, _, _) =>
            {
                using AppDbContext verify = new(options);
                verify.PrintJobs.Single(j => j.Id == jobId).Status.Should().Be(PrintJobStatus.Failed);
            })
            .Returns(Task.CompletedTask);
        await using AppDbContext db = new(options);
        PrintJobCompletionService service = new(
            db,
            Hub(),
            NullLogger<PrintJobCompletionService>.Instance,
            backendFactory: backendFactory.Object,
            spoolmanService: spoolman.Object,
            coverageBroadcaster: broadcaster.Object);

        bool failed = await service.MarkCurrentJobAsFailedAsync(
            printerId,
            "printer error",
            new PrinterTerminalObservation(null),
            CancellationToken.None);

        failed.Should().BeTrue();
        broadcaster.Verify(
            b => b.BroadcastPrinterChangedAsync(
                printerId,
                FilamentCoverageChangeReasons.QueueChanged,
                It.IsAny<CancellationToken>()),
            Times.Once);
        broadcaster.Verify(
            b => b.BroadcastPrinterChangedAsync(
                printerId,
                FilamentCoverageChangeReasons.SpoolWeight,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static PrintJob Job(Guid printerId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "job",
        AssignedPrinterId = printerId,
        Status = PrintJobStatus.Assigned,
        Copies = 1,
        CompletedCopies = 0,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        QueuedAt = DateTime.UtcNow,
    };

    private static PrintJobManagementService QueueService(
        IPrintJobManagementRepository repository,
        IFilamentCoverageBroadcaster broadcaster,
        IPrintersService? printersService = null)
        => new(
            repository,
            NullLogger<PrintJobManagementService>.Instance,
            printersService ?? Mock.Of<IPrintersService>(),
            Mock.Of<IStoragePathService>(),
            Hub(),
            Mock.Of<IStoredFileOperationsService>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            coverageBroadcaster: broadcaster);

    private static IHubContext<PrinterHub> Hub()
    {
        Mock<IClientProxy> proxy = new();
        proxy
            .Setup(p => p.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IHubClients> clients = new();
        clients.Setup(c => c.All).Returns(proxy.Object);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return hub.Object;
    }
}
