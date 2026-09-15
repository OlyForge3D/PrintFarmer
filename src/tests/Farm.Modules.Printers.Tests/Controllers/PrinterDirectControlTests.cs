using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Modules.Printers.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.Printers.Tests.Controllers;

public sealed class PrinterDirectControlTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly string connectionString = $"Data Source=direct_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=False";
    private readonly Guid printerId = Guid.NewGuid();
    private readonly Guid userId = Guid.NewGuid();
    private readonly Mock<IQueueResourceAuthorizationService> authorization = new();
    private readonly Mock<IPrintersService> printers = new();
    private SqliteConnection keepAlive = null!;

    public async Task InitializeAsync()
    {
        keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        authorization.Setup(service => service.CanActorAccessPrinterAsync(
            It.IsAny<string>(), printerId, PrinterGroupAccessLevel.Submit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        printers.Setup(service => service.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer { Id = printerId, Backend = (int)PrinterBackend.Moonraker });
        printers.Setup(service => service.GetStatusDtoAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(printerId, true, "Idle", X: 10, Y: 20, Z: 0));
        await using AppDbContext db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        db.Printers.Add(new Printer { Id = printerId, Name = "Direct motion", Backend = (int)PrinterBackend.Moonraker });
        db.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printerId });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await keepAlive.DisposeAsync();

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();

    [Theory]
    [InlineData("success")]
    [InlineData("false")]
    [InlineData("exception")]
    [InlineData("cancel")]
    [InlineData("timeout")]
    public async Task HomeAsync_BackendOutcome_ReleasesBarrierOnlyAfterIoWithoutReplay(string outcome)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        var clock = new DirectControlTestClock();
        printers.Setup(service => service.SendHomeAsync(printerId, It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, CancellationToken token) =>
            {
                using CancellationTokenRegistration registration = token.Register(() => cancellationObserved.TrySetResult());
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
                token.ThrowIfCancellationRequested();
                if (outcome == "exception")
                {
                    throw new IOException("Response lost");
                }

                return outcome == "success";
            });
        await using AppDbContext commandDb = CreateContext();
        PrintersController controller = CreateController(commandDb, clock);
        Task<ActionResult<CommandResult>> pending = controller.HomeAsync(printerId, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await AssertBarrierBlocksCompetitorAsync();
            if (outcome == "cancel")
            {
                caller.Cancel();
            }
            else if (outcome == "timeout")
            {
                clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
                Assert.False(pending.IsCompleted);
                Assert.False(cancellationObserved.Task.IsCompleted);
                clock.Advance(TimeSpan.FromSeconds(1));
            }

            if (outcome is "cancel" or "timeout")
            {
                await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(pending.IsCompleted);
                await AssertBarrierBlocksCompetitorAsync();
            }
        }
        finally
        {
            release.TrySetResult();
        }

        if (outcome == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        else
        {
            ActionResult<CommandResult> response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            if (outcome == "success")
            {
                Assert.True(Assert.IsType<CommandResult>(response.Value).Success);
            }
            else
            {
                ObjectResult error = Assert.IsType<ObjectResult>(response.Result);
                Assert.Equal(503, error.StatusCode);
                Assert.False(Assert.IsType<CommandResult>(error.Value).Success);
            }
        }

        await using AppDbContext verify = CreateContext();
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync();
        Assert.Null(state.PhysicalControlCommandId);
        Assert.False(state.PhysicalControlRequiresReconciliation);
        string eventType = outcome == "success"
            ? PrinterPhysicalActuationService.EventTypeCompleted
            : PrinterPhysicalActuationService.EventTypeUnknown;
        Assert.Single(await verify.QueueDispatchOutbox.Where(row => row.EventType == eventType).ToListAsync());
        if (outcome != "success")
        {
            Assert.False(await verify.QueueDispatchOutbox.AnyAsync(row => row.EventType == PrinterPhysicalActuationService.EventTypeCompleted));
        }

        PrinterActuationResult next = await CreateActuation(verify).AcquireDirectAsync(printerId, userId.ToString(), "move");
        Assert.True(next.Success);
        Assert.NotNull(next.Lease);
        await CreateActuation(verify).CompleteDirectAsync(next.Lease, false);
        printers.Verify(service => service.SendHomeAsync(printerId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("home_xy")]
    [InlineData("home_z")]
    [InlineData("move")]
    [InlineData("move_to")]
    public async Task MarkDirectUnknownAsync_ManualCommand_ReleasesBarrierAndPreservesUnknown(string operation)
    {
        await using AppDbContext db = CreateContext();
        PrinterPhysicalActuationService service = CreateActuation(db);
        PrinterActuationResult acquired = await service.AcquireDirectAsync(printerId, userId.ToString(), operation);
        Assert.NotNull(acquired.Lease);
        await service.MarkDirectUnknownAsync(acquired.Lease, "backend_unreachable");
        Assert.Null((await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
        Assert.Single(await db.QueueDispatchOutbox.Where(row => row.EventType == PrinterPhysicalActuationService.EventTypeUnknown).ToListAsync());
        Assert.False(await db.QueueDispatchOutbox.AnyAsync(row => row.EventType == PrinterPhysicalActuationService.EventTypeCompleted));
    }

    [Fact]
    public async Task HomeAsync_AuthorizationDenied_ProducesNoBackendIoOrBarrier()
    {
        authorization.Setup(service => service.CanActorAccessPrinterAsync(
            It.IsAny<string>(), printerId, PrinterGroupAccessLevel.Submit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        await using AppDbContext db = CreateContext();
        ActionResult<CommandResult> response = await CreateController(db).HomeAsync(printerId, default);
        Assert.Equal(404, Assert.IsAssignableFrom<ObjectResult>(response.Result).StatusCode);
        printers.Verify(service => service.SendHomeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null((await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
    }

    [Theory]
    [InlineData(double.NaN, null)]
    [InlineData(double.PositiveInfinity, null)]
    [InlineData(double.NegativeInfinity, null)]
    [InlineData(1d, 0d)]
    [InlineData(1d, -1d)]
    [InlineData(1d, double.NaN)]
    [InlineData(1d, double.PositiveInfinity)]
    [InlineData(null, null)]
    public async Task ManualMoveAsync_InvalidInput_RejectsBothRoutesBeforeAcquiring(double? x, double? feedrate)
    {
        var backend = new Mock<IPrintersService>(MockBehavior.Strict);
        var actuation = new Mock<IPrinterPhysicalActuationService>(MockBehavior.Strict);
        PrintersController controller = PrintersControllerControlGuardsTests.CreateController(
            backend, new Mock<IPrinterStatusCacheReader>(), out _, realActuation: actuation.Object);
        var request = new MoveRequest(x, null, null, feedrate);

        Assert.IsType<BadRequestObjectResult>((await controller.MoveAsync(printerId, request, default)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.MoveToAsync(printerId, request, default)).Result);

        backend.VerifyNoOtherCalls();
        actuation.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualMoveAsync_Moonraker_ValidatesFreshDestinationWithoutClearancePolicy(bool absolute)
    {
        var observed = new PrinterStatusDto(printerId, true, "Idle", X: 10, Y: 20, Z: 0);
        printers.Setup(service => service.GetStatusDtoAsync(printerId, It.IsAny<CancellationToken>())).ReturnsAsync(observed);
        printers.Setup(service => service.MoveAsync(printerId, 2, null, 0.2, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterControlOutcome.Ok);
        printers.Setup(service => service.MoveToAsync(printerId, 2, null, 0.2, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterControlOutcome.Ok);
        var safety = new Mock<IPrinterSafetyGuard>(MockBehavior.Strict);
        var expected = absolute
            ? new PrinterSafetyMoveRequest(2, null, 0.2)
            : new PrinterSafetyMoveRequest(12, 20, 0.2);
        safety.Setup(service => service.ValidateObservedManualMoveAsync(
            printerId, expected, observed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterSafetyValidationResult.Allowed);
        await using AppDbContext db = CreateContext();
        PrintersController controller = PrintersControllerControlGuardsTests.CreateController(
            printers, new Mock<IPrinterStatusCacheReader>(MockBehavior.Strict), out _,
            safetyGuard: safety, realActuation: CreateActuation(db));
        var request = new MoveRequest(2, null, 0.2, null);

        ActionResult<CommandResult> response = absolute
            ? await controller.MoveToAsync(printerId, request, default)
            : await controller.MoveAsync(printerId, request, default);

        Assert.True(Assert.IsType<CommandResult>(response.Value).Success);
        printers.Verify(service => service.GetStatusDtoAsync(printerId, It.IsAny<CancellationToken>()), Times.Once);
        safety.Verify(service => service.ValidateObservedManualMoveAsync(
            printerId, expected, observed, It.IsAny<CancellationToken>()), Times.Once);
        safety.Verify(service => service.ValidateAsync(
            It.IsAny<Guid>(), It.IsAny<PrinterSafetyOperation>(), It.IsAny<PrinterSafetyMoveRequest?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null((await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
    }

    [Theory]
    [InlineData("Printing", true)]
    [InlineData("Idle", false)]
    [InlineData("unknown", true)]
    public async Task HomeAsync_FreshStatusNotReady_DoesNotSend(string state, bool online)
    {
        printers.Setup(service => service.GetStatusDtoAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(printerId, online, state));
        await using AppDbContext db = CreateContext();
        ActionResult<CommandResult> response = await CreateController(db).HomeAsync(printerId, default);
        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(response.Result).StatusCode);
        Assert.Null((await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
        printers.Verify(service => service.SendHomeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("printer_not_homed")]
    [InlineData("printer_travel_limit")]
    public async Task MoveToAsync_SafetyRejected_ReleasesBarrierWithoutBackendIo(string code)
    {
        var safety = new Mock<IPrinterSafetyGuard>();
        safety.Setup(service => service.ValidateObservedManualMoveAsync(
            printerId, It.IsAny<PrinterSafetyMoveRequest>(),
            It.IsAny<PrinterStatusDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterSafetyValidationResult.Reject(409, code, "Unsafe move."));
        await using AppDbContext db = CreateContext();
        PrintersController controller = PrintersControllerControlGuardsTests.CreateController(
            printers, new Mock<IPrinterStatusCacheReader>(), out _, safetyGuard: safety, realActuation: CreateActuation(db));
        ActionResult<CommandResult> response = await controller.MoveToAsync(
            printerId, new MoveRequest(1, null, null, null), default);
        ObjectResult error = Assert.IsAssignableFrom<ObjectResult>(response.Result);
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(code, Assert.IsType<ProblemDetails>(error.Value).Extensions["code"]);
        Assert.Null((await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
        printers.Verify(service => service.MoveToAsync(
            It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(),
            It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AcquireDirectAsync_ActiveDispatch_RejectsWithoutBackendIo()
    {
        await using AppDbContext db = CreateContext();
        PrinterDispatchState state = await db.PrinterDispatchStates.SingleAsync();
        state.ActiveDispatchAttemptId = Guid.NewGuid();
        await db.SaveChangesAsync();
        ActionResult<CommandResult> response = await CreateController(db).HomeAsync(printerId, default);
        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(response.Result).StatusCode);
        printers.Verify(service => service.SendHomeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(state.PhysicalControlCommandId);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("home_xy")]
    [InlineData("home_z")]
    [InlineData("move")]
    [InlineData("move_to")]
    public async Task AcquireDirectAsync_ExpiredManualOrphan_ReclaimsWithUnknownAuditWithoutReplay(string operation)
    {
        await using AppDbContext db = CreateContext();
        PrinterActuationLease previous = await SeedBarrierAsync(db, operation, DateTime.UtcNow.AddMinutes(-6));
        PrinterPhysicalActuationService service = CreateActuation(db);

        PrinterActuationResult acquired = await service.AcquireDirectAsync(printerId, userId.ToString(), "move");

        Assert.True(acquired.Success);
        Assert.NotNull(acquired.Lease);
        Assert.NotEqual(previous.CommandId, acquired.Lease.CommandId);
        PrinterDispatchState state = await db.PrinterDispatchStates.SingleAsync();
        Assert.Equal(acquired.Lease.CommandId, state.PhysicalControlCommandId);
        Assert.False(state.PhysicalControlRequiresReconciliation);
        QueueOperationAudit audit = Assert.Single(await db.QueueOperationAudits
            .Where(row => row.ReasonCode == "direct_manual_command_expired").ToListAsync());
        Assert.Equal(QueueAuditOutcomes.Unknown, audit.Outcome);
        Assert.Single(await db.QueueDispatchOutbox.Where(row => row.EventType == PrinterPhysicalActuationService.EventTypeStarted).ToListAsync());
        Assert.False(await db.QueueDispatchOutbox.AnyAsync(row => row.EventType == PrinterPhysicalActuationService.EventTypeCompleted));
        printers.Verify(service => service.SendHomeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        printers.Verify(service => service.MoveAsync(It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<double?>(),
            It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("shutdown-grace")]
    [InlineData("missing-start")]
    [InlineData("attempt-bound")]
    [InlineData("unrelated")]
    [InlineData("active-attempt")]
    [InlineData("active-job-pointer")]
    [InlineData("active-job")]
    public async Task AcquireDirectAsync_NonReclaimableFence_PreservesOwnership(string condition)
    {
        await using AppDbContext db = CreateContext();
        DateTime? started = condition switch
        {
            "current" => DateTime.UtcNow,
            "shutdown-grace" => DateTime.UtcNow.AddMinutes(-5).AddSeconds(-15),
            "missing-start" => null,
            _ => DateTime.UtcNow.AddMinutes(-10),
        };
        string operation = condition == "unrelated" ? "printer_file_delete" : "home";
        PrinterActuationLease previous = await SeedBarrierAsync(db, operation, started,
            condition == "attempt-bound" ? Guid.NewGuid() : null);
        PrinterDispatchState state = await db.PrinterDispatchStates.SingleAsync();
        if (condition == "active-attempt")
        {
            state.ActiveDispatchAttemptId = Guid.NewGuid();
        }
        else if (condition == "active-job-pointer")
        {
            state.ActiveJobId = Guid.NewGuid();
        }
        else if (condition == "active-job")
        {
            db.PrintJobs.Add(new PrintJob
            {
                Id = Guid.NewGuid(), Name = "Active job", AssignedPrinterId = printerId,
                Status = PrintJobStatus.Printing,
            });
        }

        await db.SaveChangesAsync();
        PrinterActuationResult result = await CreateActuation(db).AcquireDirectAsync(printerId, userId.ToString(), "move");

        Assert.False(result.Success);
        Assert.Null(result.Lease);
        Assert.Equal(previous.CommandId, state.PhysicalControlCommandId);
        Assert.Equal(previous.AttemptId, state.PhysicalControlAttemptId);
        Assert.False(await db.QueueOperationAudits.AnyAsync(row => row.ReasonCode == "direct_manual_command_expired"));
        Assert.False(await db.QueueDispatchOutbox.AnyAsync());
    }

    [Theory]
    [InlineData("home")]
    [InlineData("move_to")]
    public async Task AcquireDirectAsync_ConcurrentExpiredReclaim_OnlyOneRevisionWins(string operation)
    {
        await using (AppDbContext seed = CreateContext())
        {
            await SeedBarrierAsync(seed, operation, DateTime.UtcNow.AddMinutes(-6));
        }

        await using AppDbContext first = CreateContext();
        await using AppDbContext stale = CreateContext();
        await first.PrinterDispatchStates.LoadAsync();
        await stale.PrinterDispatchStates.LoadAsync();

        PrinterActuationResult winner = await CreateActuation(first).AcquireDirectAsync(printerId, userId.ToString(), "move");
        PrinterActuationResult loser = await CreateActuation(stale).AcquireDirectAsync(printerId, userId.ToString(), "home");

        Assert.True(winner.Success);
        Assert.NotNull(winner.Lease);
        Assert.Equal(PrinterActuationResultCode.ConcurrencyConflict, loser.Code);
        Assert.Null(loser.Lease);
        await using AppDbContext verify = CreateContext();
        Assert.Equal(winner.Lease.CommandId, (await verify.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
        Assert.Single(await verify.QueueOperationAudits.Where(row => row.ReasonCode == "direct_manual_command_expired").ToListAsync());
        Assert.Single(await verify.QueueDispatchOutbox.Where(row => row.EventType == PrinterPhysicalActuationService.EventTypeStarted).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteDirectAsync_ExpiredPriorSender_CannotClearSuccessor(bool unknown)
    {
        PrinterActuationLease previous;
        await using (AppDbContext seed = CreateContext())
        {
            previous = await SeedBarrierAsync(seed, "home", DateTime.UtcNow.AddMinutes(-6));
        }

        await using AppDbContext successorDb = CreateContext();
        PrinterActuationResult successor = await CreateActuation(successorDb).AcquireDirectAsync(printerId, userId.ToString(), "move");
        Assert.NotNull(successor.Lease);
        await using (AppDbContext lateDb = CreateContext())
        {
            if (unknown)
            {
                await CreateActuation(lateDb).MarkDirectUnknownAsync(previous, "late_response");
            }
            else
            {
                await CreateActuation(lateDb).CompleteDirectAsync(previous, true);
            }
        }

        await using AppDbContext verify = CreateContext();
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync();
        Assert.Equal(successor.Lease.CommandId, state.PhysicalControlCommandId);
        Assert.Equal("move", state.PhysicalControlOperation);
        Assert.False(state.PhysicalControlRequiresReconciliation);
        Assert.False(await verify.QueueDispatchOutbox.AnyAsync(row =>
            row.EventType == PrinterPhysicalActuationService.EventTypeCompleted ||
            row.EventType == PrinterPhysicalActuationService.EventTypeUnknown));
    }

    private async Task<PrinterActuationLease> SeedBarrierAsync(
        AppDbContext db, string operation, DateTime? started, Guid? attemptId = null)
    {
        var lease = new PrinterActuationLease(Guid.NewGuid(), printerId, attemptId, operation, userId.ToString());
        PrinterDispatchState state = await db.PrinterDispatchStates.SingleAsync();
        state.PhysicalControlCommandId = lease.CommandId;
        state.PhysicalControlAttemptId = attemptId;
        state.PhysicalControlOperation = operation;
        state.PhysicalControlActorSubject = userId.ToString();
        state.PhysicalControlStartedAtUtc = started;
        await db.SaveChangesAsync();
        return lease;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkDirectUnknownAsync_UnrelatedOrAttemptBoundCommand_PreservesFence(bool attemptBound)
    {
        await using AppDbContext db = CreateContext();
        Guid commandId = Guid.NewGuid();
        Guid? attemptId = attemptBound ? Guid.NewGuid() : null;
        string operation = attemptBound ? "home" : "printer_file_delete";
        PrinterDispatchState state = await db.PrinterDispatchStates.SingleAsync();
        state.PhysicalControlCommandId = commandId;
        state.PhysicalControlAttemptId = attemptId;
        state.PhysicalControlOperation = operation;
        await db.SaveChangesAsync();
        await CreateActuation(db).MarkDirectUnknownAsync(
            new PrinterActuationLease(commandId, printerId, attemptId, operation, userId.ToString()), "response_lost");
        Assert.Equal(commandId, state.PhysicalControlCommandId);
        Assert.Equal(attemptId, state.PhysicalControlAttemptId);
        Assert.True(state.PhysicalControlRequiresReconciliation);
    }

    private async Task AssertBarrierBlocksCompetitorAsync()
    {
        await using AppDbContext db = CreateContext();
        Assert.NotNull((await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
        PrinterActuationResult competing = await CreateActuation(db).AcquireDirectAsync(printerId, userId.ToString(), "move");
        Assert.Equal(PrinterActuationResultCode.FenceConflict, competing.Code);
        Assert.Null(competing.Lease);
    }

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options);

    private PrinterPhysicalActuationService CreateActuation(AppDbContext db) =>
        new(db, new DbOutboxSequenceAllocator(), authorization.Object, NullLogger<PrinterPhysicalActuationService>.Instance);

    private PrintersController CreateController(AppDbContext db, TimeProvider? clock = null)
    {
        PrintersController controller = PrintersControllerControlGuardsTests.CreateController(
            printers, new Mock<IPrinterStatusCacheReader>(), out _, timeProvider: clock, realActuation: CreateActuation(db));
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        return controller;
    }
}
