using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Webhooks;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.AutoDispatch;

public sealed class AutoDispatchServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public AutoDispatchServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task MarkPreClearAsync_WhenQueuedJobsAlreadyExist_NotifiesDispatchTrigger()
    {
        Printer printer = await CreatePrinterAsync();
        await CreateQueuedJobAsync(printer, "queued-job", queuePosition: 1);

        var (hubContext, _) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            dispatchTrigger: dispatchTrigger.Object);

        AutoDispatchStatusDto status = await service.MarkPreClearAsync(printer.Id);

        status.BedPreConfirmed.Should().BeTrue();
        dispatchTrigger.Verify(trigger => trigger.NotifyJobQueued(printer.Id), Times.Once);
    }

    [Fact]
    public async Task TransitionToPendingReadyAsync_WhenQueuedJobsExistAndBedIsNotPreCleared_SetsPendingReadyAndBroadcastsStatus()
    {
        Printer printer = await CreatePrinterAsync();
        await CreateQueuedJobAsync(printer, "queued-job-1", queuePosition: 1);

        var (hubContext, clientProxy) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        Mock<IWebhookService> webhookService = new();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            webhookService: webhookService.Object,
            dispatchTrigger: dispatchTrigger.Object);

        await service.TransitionToPendingReadyAsync(printer.Id);

        Printer persistedPrinter = await _db.Printers.Include(p => p.DispatchState).SingleAsync(p => p.Id == printer.Id);
        persistedPrinter.DispatchState!.AutoDispatchState.Should().Be(AutoDispatchState.PendingReady);
        persistedPrinter.DispatchState!.BedPreConfirmed.Should().BeFalse();

        clientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                "autodispatchstatechanged",
                It.Is<object?[]>(args => MatchesStatusEvent(
                    args,
                    printer.Id,
                    nameof(AutoDispatchState.PendingReady),
                    1,
                    "Bed Clear Confirmed",
                    false,
                    "Waiting for operator")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        webhookService.Verify(service => service.Enqueue("printer.autodispatch_pending", It.IsAny<object>()), Times.Once);
        dispatchTrigger.Verify(trigger => trigger.NotifyJobQueued(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task MarkReadyAsync_WhenPrinterIsPendingReadyWithQueuedJob_TransitionsToReadyAndNotifiesDispatchTrigger()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState { PrinterId = printer.Id, AutoDispatchState = AutoDispatchState.PendingReady };
        await _db.SaveChangesAsync();

        PrintJob queuedJob = await CreateQueuedJobAsync(printer, "queued-job-1", queuePosition: 1);

        var (hubContext, clientProxy) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            dispatchTrigger: dispatchTrigger.Object);

        AutoDispatchReadyResult result = await service.MarkReadyAsync(printer.Id);

        result.Status.State.Should().Be(nameof(AutoDispatchState.Ready));
        result.NextJob.Should().NotBeNull();
        result.NextJob!.Id.Should().Be(queuedJob.Id);
        result.FilamentCheck.Should().NotBeNull();
        result.FilamentCheck!.Sufficient.Should().BeTrue();

        Printer persistedPrinter = await _db.Printers.Include(p => p.DispatchState).SingleAsync(p => p.Id == printer.Id);
        persistedPrinter.DispatchState!.AutoDispatchState.Should().Be(AutoDispatchState.Ready);

        clientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                "autodispatchstatechanged",
                It.Is<object?[]>(args => MatchesStatusEvent(
                    args,
                    printer.Id,
                    nameof(AutoDispatchState.Ready),
                    1)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        dispatchTrigger.Verify(trigger => trigger.NotifyJobQueued(printer.Id), Times.Once);
    }

    [Fact]
    public async Task SkipNextJobAsync_WhenQueuedJobsRemain_StaysPendingReadyAndCancelsOnlyNextJob()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState { PrinterId = printer.Id, AutoDispatchState = AutoDispatchState.PendingReady };
        await _db.SaveChangesAsync();

        PrintJob firstJob = await CreateQueuedJobAsync(printer, "queued-job-1", queuePosition: 1);
        PrintJob secondJob = await CreateQueuedJobAsync(printer, "queued-job-2", queuePosition: 2);

        var (hubContext, clientProxy) = CreateHubContextMockWithProxy();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance);

        AutoDispatchStatusDto status = await service.SkipNextJobAsync(printer.Id);

        status.State.Should().Be(nameof(AutoDispatchState.PendingReady));
        status.QueueDepth.Should().Be(1);
        status.ReadyGateChecks.Should().Contain(check =>
            check.Name == "Jobs in Queue"
            && check.Passed
            && check.Message.Contains("1 job queued"));

        PrintJob persistedFirstJob = await _db.PrintJobs.SingleAsync(job => job.Id == firstJob.Id);
        PrintJob persistedSecondJob = await _db.PrintJobs.SingleAsync(job => job.Id == secondJob.Id);
        persistedFirstJob.Status.Should().Be(PrintJobStatus.Cancelled);
        persistedSecondJob.Status.Should().Be(PrintJobStatus.Queued);

        Printer persistedPrinter = await _db.Printers.Include(p => p.DispatchState).SingleAsync(p => p.Id == printer.Id);
        persistedPrinter.DispatchState!.AutoDispatchState.Should().Be(AutoDispatchState.PendingReady);

        clientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                "autodispatchstatechanged",
                It.Is<object?[]>(args => MatchesStatusEvent(
                    args,
                    printer.Id,
                    nameof(AutoDispatchState.PendingReady),
                    1)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetStatusAsync_WhenPrinterIsPendingReady_PopulatesAttentionDetails()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState { PrinterId = printer.Id, AutoDispatchState = AutoDispatchState.PendingReady };
        await _db.SaveChangesAsync();
        await CreateQueuedJobAsync(printer, "queued-job-1", queuePosition: 1);

        var (hubContext, _) = CreateHubContextMockWithProxy();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance);

        AutoDispatchStatusDto status = await service.GetStatusAsync(printer.Id);

        status.AttentionMessage.Should().Be("Print completed. 1 queued job is blocked until you clear the bed and confirm ready. Once confirmed, the next queued job will start automatically.");
    }

    [Fact]
    public async Task MarkPreClearAsync_WhenQueuedJobExists_PopulatesReadyAttentionMessage()
    {
        Printer printer = await CreatePrinterAsync();
        await CreateQueuedJobAsync(printer, "queued-job-1", queuePosition: 1);

        var (hubContext, _) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            dispatchTrigger: dispatchTrigger.Object);

        AutoDispatchStatusDto status = await service.MarkPreClearAsync(printer.Id);

        status.BedPreConfirmed.Should().BeTrue();
        status.AttentionMessage.Should().Be("Bed is clear. The next queued job will start automatically.");
    }

    [Fact]
    public async Task GetStatusAsync_WhenPrinterIsInMaintenanceWithQueuedJob_PopulatesAttentionMessage()
    {
        Printer printer = await CreatePrinterAsync();
        printer.InMaintenance = true;
        await _db.SaveChangesAsync();
        await CreateQueuedJobAsync(printer, "queued-job-1", queuePosition: 1);

        var (hubContext, _) = CreateHubContextMockWithProxy();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance);

        AutoDispatchStatusDto status = await service.GetStatusAsync(printer.Id);

        status.AttentionMessage.Should().Be("Printer is in maintenance mode. 1 queued job will not start until maintenance is complete and the printer is available.");
    }

    private async Task<Printer> CreatePrinterAsync()
    {
        Manufacturer manufacturer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Manufacturer",
        };
        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Model",
            ManufacturerId = manufacturer.Id,
        };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "AutoDispatch Service Test Printer",
            ServerUrl = $"http://autodispatch-service-test-{Guid.NewGuid():N}.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            AutoDispatchEnabled = true,
            IsEnabled = true,
            IsAvailable = true,
        };

        _db.Manufacturers.Add(manufacturer);
        _db.PrinterModels.Add(model);
        _db.Printers.Add(printer);
        await _db.SaveChangesAsync();

        return printer;
    }

    private async Task<PrintJob> CreateQueuedJobAsync(Printer printer, string name, int queuePosition)
    {
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            Priority = 0,
            QueuePosition = queuePosition,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };

        _db.PrintJobs.Add(job);
        await _db.SaveChangesAsync();
        return job;
    }

    private static (Mock<IHubContext<PrinterHub>> Hub, Mock<IClientProxy> Proxy) CreateHubContextMockWithProxy()
    {
        Mock<IClientProxy> proxy = new();
        proxy.Setup(x => x.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IHubClients> clients = new();
        clients.Setup(x => x.All).Returns(proxy.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(x => x.Clients).Returns(clients.Object);
        return (hub, proxy);
    }

    private static bool MatchesStatusEvent(
        object?[] args,
        Guid printerId,
        string expectedState,
        int expectedQueueDepth,
        string? gateName = null,
        bool? gatePassed = null,
        string? gateMessageFragment = null)
    {
        if (args.Length != 1)
        {
            return false;
        }

        AutoDispatchStatusDto? status = args[0] as AutoDispatchStatusDto;
        if (status is null)
        {
            return false;
        }

        if (status.PrinterId != printerId || status.State != expectedState || status.QueueDepth != expectedQueueDepth)
        {
            return false;
        }

        if (gateName is null)
        {
            return true;
        }

        ReadyGateCheckDto? gate = status.ReadyGateChecks.FirstOrDefault(check => check.Name == gateName);
        if (gate is null)
        {
            return false;
        }

        if (gatePassed.HasValue && gate.Passed != gatePassed.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(gateMessageFragment) && !gate.Message.Contains(gateMessageFragment, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}
