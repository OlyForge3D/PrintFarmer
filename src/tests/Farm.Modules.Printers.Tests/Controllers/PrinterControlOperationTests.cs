using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Security;
using Farm.Modules.Printers.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Farm.Modules.Printers.Tests.Controllers;

public sealed class PrinterControlOperationTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly SqliteConnection keepAlive;
    private readonly string connectionString = $"Data Source=motion_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=False";
    private readonly Guid printerId = Guid.NewGuid();
    private readonly Guid userId = Guid.NewGuid();
    private readonly Mock<IQueueResourceAuthorizationService> authorization = new();
    private readonly Mock<IAuthenticationService> authentication = new();
    private readonly Mock<IPrinterSafetyGuard> safety = new();
    private readonly FakeChannel channel = new();
    private readonly SnapshotCommands commands = new();
    private ServiceProvider provider = null!;

    public PrinterControlOperationTests()
    {
        keepAlive = new SqliteConnection(connectionString);
        authorization.Setup(a => a.CanActorAccessPrinterAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        authorization.Setup(a => a.CanAccessPrinterAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<Guid>(), It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        authentication.Setup(a => a.HasPermissionAsync(userId, "queue", "start")).ReturnsAsync(true);
        safety.Setup(guard => guard.ValidateObservedMoveAsync(printerId,
            It.IsAny<PrinterSafetyMoveRequest>(), It.IsAny<PrinterStatusDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterSafetyValidationResult.Allowed);
        authentication.Setup(a => a.HasPermissionAsync(userId, "queue", "cancel")).ReturnsAsync(true);
    }

    public async Task InitializeAsync()
    {
        await keepAlive.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString).AddInterceptors(commands));
        services.AddSingleton(authorization.Object);
        services.AddSingleton(authentication.Object);
        services.AddSingleton(safety.Object);
        services.AddSingleton<IDbOutboxSequenceAllocator, DbOutboxSequenceAllocator>();
        services.AddScoped<PrinterControlOperationService>();
        var factory = new Mock<IMoonrakerMotionChannelFactory>();
        factory.Setup(f => f.ConnectAsync(It.IsAny<Printer>(), It.IsAny<CancellationToken>())).ReturnsAsync(channel);
        services.AddSingleton(factory.Object);
        provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Printers.Add(new Printer { Id = printerId, Name = "Motion test", Backend = (int)PrinterBackend.Moonraker, IsEnabled = true, ServerUrl = "http://test.invalid", BackendPort = 7125 });
        db.Users.Add(new User { Id = userId, Username = userId.ToString(), Email = $"{userId}@example.invalid", IsActive = true });
        db.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printerId });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await channel.DisposeAsync();
        await provider.DisposeAsync();
        await keepAlive.DisposeAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();

    [Fact]
    public async Task AdmitAsync_DuplicateIntent_PersistsOneOperationBarrierAuditAndOutbox()
    {
        Guid id = Guid.NewGuid();
        PrinterControlOperationDto first = await AdmitAsync(id);
        PrinterControlOperationDto replay = await AdmitAsync(id);
        Assert.Equal(first, replay);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.PrinterControlOperations.CountAsync());
        Assert.Equal(id, (await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
        Assert.Equal(1, await db.QueueOperationAudits.CountAsync());
        Assert.Equal(1, await db.QueueDispatchOutbox.CountAsync());
        Assert.Null((await db.PrinterControlOperations.SingleAsync()).SendCommittedAtUtc);
    }

    [Fact]
    public async Task ClaimAsync_PrivateOwnership_DoesNotChangeReceiptRevisionOrPublicEvents()
    {
        Guid id = Guid.NewGuid();
        PrinterControlOperationDto admitted = await AdmitAsync(id);
        await ChangeAsync(async (db, service) =>
        {
            Guid owner = Guid.NewGuid();
            Assert.NotNull(await service.ClaimAsync(id, owner, default));
            Assert.Null(await service.ClaimAsync(id, Guid.NewGuid(), default));
            Assert.Equal(admitted.RowVersion, (await service.GetAsync(printerId, id, default)).RowVersion);
            Assert.Equal(1, await db.QueueDispatchOutbox.CountAsync());
            Assert.Equal(1, await db.QueueOperationAudits.CountAsync());
        });
    }

    [Fact]
    public async Task MissingPlugin_HostServicesResolve_AdmissionExplicitlyUnsupportedWithoutSend()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton(authorization.Object);
        services.AddSingleton(authentication.Object);
        services.AddSingleton<IDbOutboxSequenceAllocator, DbOutboxSequenceAllocator>();
        services.AddScoped<PrinterControlOperationService>();
        services.AddHostedService<PrinterControlOperationWorker>();
        await using ServiceProvider missing = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        IHostedService worker = Assert.Single(missing.GetServices<IHostedService>());
        await worker.StartAsync(default);
        await using AsyncServiceScope scope = missing.CreateAsyncScope();
        PrinterControlException error = await Assert.ThrowsAsync<PrinterControlException>(() =>
            scope.ServiceProvider.GetRequiredService<PrinterControlOperationService>().AdmitAsync(
                printerId, Guid.NewGuid(), userId.ToString(), new(PrinterControlKind.HomeAll), default));
        Assert.Equal("printer_operation_unsupported", error.Code);
        Assert.Equal(422, error.Status);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().PrinterControlOperations.CountAsync());
        Guid queuedBeforePluginLoss = Guid.NewGuid();
        await AdmitAsync(queuedBeforePluginLoss);
        await WaitForStateAsync(queuedBeforePluginLoss, PrinterControlState.Failed);
        PrinterControlOperationDto failed = await GetAsync(queuedBeforePluginLoss);
        Assert.Equal("printer_operation_unsupported", failed.Failure?.Code);
        Assert.Equal(PrinterControlEvidence.NotSent, failed.CompletionEvidence);
        await worker.StopAsync(default);
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData(null, 2d, 3d)]
    [InlineData(1d, null, 3d)]
    [InlineData(1d, 2d, null)]
    public async Task ControllerAsync_PartialMoveTo_Returns400WithoutAdmission(double? x, double? y, double? z)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        PrinterControlOperationsController controller = CreateMotionController(scope.ServiceProvider);
        var result = Assert.IsType<ObjectResult>(await controller.SubmitAsync(printerId,
            new(PrinterControlKind.MoveTo, x, y, z), Guid.NewGuid().ToString(), default));
        Assert.Equal(400, result.StatusCode);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().PrinterControlOperations.CountAsync());
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData("oversized", false)]
    [InlineData("outside", false)]
    [InlineData("unhomed", false)]
    [InlineData("stale", false)]
    [InlineData("stale_frame", false)]
    [InlineData("offset_target", false)]
    [InlineData("missing_position", false)]
    [InlineData("outside_current", false)]
    [InlineData("multi_axis", true)]
    public async Task ControllerAndWorkerAsync_Jog_ValidatesFreshObservedTargetBeforeAnySend(string scenario, bool allowed)
    {
        DateTime now = DateTime.UtcNow;
        PrinterStatusDto observed = await channel.ReadMotionStateAsync(printerId, default);
        PrinterSafetyTelemetryDto facts = observed.SafetyTelemetry!;
        channel.MotionState = scenario switch
        {
            "unhomed" => observed with { SafetyTelemetry = facts with { HomedAxes = facts.HomedAxes with { Value = [] } } },
            "stale" => observed with { SafetyTelemetry = facts with { HomedAxes = facts.HomedAxes with { ObservedAtUtc = now.AddMinutes(-1) } } },
            "stale_frame" => observed with { SafetyTelemetry = facts with { CoordinateOriginOffsetMm = facts.CoordinateOriginOffsetMm with { ObservedAtUtc = now.AddMinutes(-1) } } },
            "offset_target" => observed with { SafetyTelemetry = facts with { CoordinateOriginOffsetMm = facts.CoordinateOriginOffsetMm with { Value = new(150, 0, 0) } } },
            "missing_position" => observed with { X = null },
            "outside_current" => observed with { X = double.MaxValue },
            _ => observed,
        };
        PrinterVerifiedSafetyDto verified = PrinterVerifiedSafetyDto.Unknown();
        verified = verified with
        {
            Operations = verified.Operations with { AbsoluteMovement = new(VerifiedSafetySupport.Supported, "test", now) },
            Positioning = new(
                new(VerifiedSafetyFactState.Verified, new(0, 0, 0), "test", now),
                new(VerifiedSafetyFactState.Verified, new(new(0, 0, 0), new(200, 200, 200)), "test", now),
                new(VerifiedSafetyFactState.Verified, 5, "test", now)),
        };
        var capabilities = new Mock<IPrinterBackendCapabilitiesService>();
        capabilities.Setup(service => service.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterBackendCapabilitiesDto(printerId, "test", PrinterBackend.Moonraker) { VerifiedSafety = verified });
        var cache = new Mock<IPrinterStatusCacheReader>(MockBehavior.Strict);
        var guard = new PrinterSafetyGuard(capabilities.Object, cache.Object, TimeProvider.System);
        safety.Setup(service => service.ValidateObservedMoveAsync(printerId, It.IsAny<PrinterSafetyMoveRequest>(),
            It.IsAny<PrinterStatusDto>(), It.IsAny<CancellationToken>()))
            .Returns((Guid printer, PrinterSafetyMoveRequest target, PrinterStatusDto snapshot, CancellationToken token) =>
                guard.ValidateObservedMoveAsync(printer, target, snapshot, token));
        double delta = scenario switch { "oversized" => double.MaxValue, "outside" => 151, "outside_current" => -double.MaxValue, _ => 1 };
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            Assert.IsType<AcceptedResult>(await CreateMotionController(scope.ServiceProvider).SubmitAsync(printerId,
                new(PrinterControlKind.Jog, delta, scenario == "multi_axis" ? 2 : null, scenario == "multi_axis" ? 3 : null),
                id.ToString(), default));
        }

        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PrinterControlOperationWorker>.Instance);
        await worker.StartAsync(default);
        if (allowed)
        {
            await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("G1 X1 Y2 Z3", channel.Script, StringComparison.Ordinal);
            channel.Completion.SetResult();
        }

        await WaitForStateAsync(id, allowed ? PrinterControlState.Succeeded : PrinterControlState.Failed);
        await worker.StopAsync(default);
        Assert.Equal(allowed ? 1 : 0, channel.SendCount);
        if (!allowed)
        {
            Assert.Equal(PrinterControlEvidence.NotSent, (await GetAsync(id)).CompletionEvidence);
        }

        cache.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AdmitAsync_ConflictingReuse_RejectsWithoutChangingBarrier()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        PrinterControlException error = await Assert.ThrowsAsync<PrinterControlException>(() =>
            scope.ServiceProvider.GetRequiredService<PrinterControlOperationService>().AdmitAsync(
                printerId, id, userId.ToString(), new(PrinterControlKind.HomeXY), default));
        Assert.Equal("idempotency_conflict", error.Code);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CurrentAsync_BarrierAndReceipt_UseOneConsistentJoinedSnapshot(bool settled, bool bulk)
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (_, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            await service.CommitSendAsync(id, owner, default);
            if (settled)
            {
                await service.SetOutcomeAsync(id, owner, true, null, default);
            }
        });
        commands.Capture = true;
        await ChangeAsync(async (db, service) =>
        {
            if (bulk)
            {
                PrinterPhysicalControlDto projection = (await PrinterControlOperationService.ProjectAsync(db, [printerId], default))[printerId];
                Assert.Equal(!settled, projection.BarrierHeld);
                Assert.Equal(settled ? null : id, projection.OperationId);
                Assert.Equal(settled ? null : PrinterControlState.Running, projection.State);
                return;
            }

            PrinterControlCurrentDto current = await service.GetCurrentAsync(printerId, default);
            AssertCoherent(current);
            Assert.Equal(!settled, current.PhysicalControl.BarrierHeld);
            Assert.Equal(settled, current.Operation is null);
        });
        commands.Capture = false;
        Assert.Single(commands.Statements, sql => sql.Contains("LEFT JOIN \"PrinterControlOperations\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CurrentAsync_ConcurrentSettlement_NeverMixesOldOperationAndReleasedBarrier()
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (_, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            await service.CommitSendAsync(id, owner, default);
        });
        Task reads = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                await ChangeAsync(async (db, service) =>
                {
                    AssertCoherent(await service.GetCurrentAsync(printerId, default));
                    PrinterPhysicalControlDto projection = (await PrinterControlOperationService.ProjectAsync(db, [printerId], default))[printerId];
                    Assert.Equal(projection.BarrierHeld, projection.OperationId.HasValue);
                    Assert.Equal(projection.BarrierHeld, projection.State == PrinterControlState.Running);
                });
            }
        });
        Task settlement = Task.Run(() => ChangeAsync(async (_, service) => await service.SetOutcomeAsync(id, owner, true, null, default)));
        await Task.WhenAll(reads, settlement);
        await ChangeAsync(async (_, service) =>
        {
            PrinterControlCurrentDto current = await service.GetCurrentAsync(printerId, default);
            AssertCoherent(current);
            Assert.Null(current.Operation);
            Assert.Equal(PrinterControlState.Succeeded, (await service.GetAsync(printerId, id, default)).State);
        });
    }

    private static void AssertCoherent(PrinterControlCurrentDto current)
    {
        Assert.Equal(current.Operation?.OperationId, current.PhysicalControl.OperationId);
        Assert.Equal(current.Operation?.State, current.PhysicalControl.State);
        if (current.Operation is { } operation)
        {
            Assert.True(current.PhysicalControl.BarrierHeld);
            Assert.Equal(operation.BarrierHeld, current.PhysicalControl.BarrierHeld);
            Assert.Equal(operation.RequiresRecovery, current.PhysicalControl.RequiresRecovery);
        }
    }

    [Theory]
    [InlineData(PrinterControlState.Running)]
    [InlineData(PrinterControlState.Unknown)]
    [InlineData(PrinterControlState.Recovering)]
    public async Task EmergencyStopAsync_DurableMotion_IsOutOfBandAndCannotReleaseOrSettleSuccessor(PrinterControlState state)
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (_, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            await service.CommitSendAsync(id, owner, default);
            if (state == PrinterControlState.Unknown)
            {
                await service.SetOutcomeAsync(id, owner, false, "transport_lost", default);
            }
            else if (state == PrinterControlState.Recovering)
            {
                PrinterControlOperationDto current = await service.GetAsync(printerId, id, default);
                await service.BeginRecoveryAsync(printerId, id, Quote(current.RowVersion), userId.ToString(), default);
            }
        });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterControlOperationService motion = scope.ServiceProvider.GetRequiredService<PrinterControlOperationService>();
        var realActuation = new PrinterPhysicalActuationService(db, scope.ServiceProvider.GetRequiredService<IDbOutboxSequenceAllocator>(),
            authorization.Object, NullLogger<PrinterPhysicalActuationService>.Instance);
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        var printers = new Mock<IPrintersService>();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        printers.Setup(service => service.EmergencyStopAsync(printerId, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(async () =>
        {
            sent.SetResult();
            return await finish.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });
        PrintersController controller = PrintersControllerControlGuardsTests.CreateController(printers,
            new Mock<IPrinterStatusCacheReader>(), out _, actuation: actuation, motionControl: motion);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        actuation.Setup(service => service.AcquireDirectAsync(printerId, userId.ToString(), "emergencystop", It.IsAny<CancellationToken>()))
            .Returns(async (Guid printer, string actor, string operation, CancellationToken token) =>
                await realActuation.AcquireDirectAsync(printer, actor, operation, token));
        Task<ActionResult<CommandResult>> stop = controller.EmergencyStopAsync(printerId, default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(stop.IsCompleted);
        Assert.False(channel.Completion.Task.IsCompleted);
        PrinterControlOperationDto during = await GetAsync(id);
        Assert.Equal(PrinterControlState.Recovering, during.State);
        Assert.True(during.BarrierHeld);
        await ChangeAsync(async (_, service) =>
        {
            await service.MaintainOwnerAsync(id, owner, true, default);
            during = await service.GetAsync(printerId, id, default);
            var error = await Assert.ThrowsAsync<PrinterControlException>(() => service.CompleteRecoveryAsync(
                printerId, id, Quote(during.RowVersion), userId.ToString(), Evidence() with { SenderIsolation = "ServiceConfirmed" }, default));
            Assert.Equal(409, error.Status);
        });
        finish.SetResult(true);
        Assert.Equal(200, Assert.IsType<ObjectResult>((await stop.WaitAsync(TimeSpan.FromSeconds(3))).Result).StatusCode);
        Assert.True((await GetAsync(id)).BarrierHeld);
        actuation.Verify(service => service.QueueLifecycleAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        actuation.Verify(service => service.CompleteDirectAsync(It.IsAny<PrinterActuationLease>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        PrinterControlOperationDto after = await GetAsync(id);
        await ChangeAsync(async (_, service) =>
            await service.CompleteRecoveryAsync(printerId, id, Quote(after.RowVersion), userId.ToString(), Evidence(), default));
        Guid successor = Guid.NewGuid();
        await AdmitAsync(successor);
        await ChangeAsync(async (context, service) =>
        {
            await service.SetOutcomeAsync(id, owner, true, null, default);
            PrinterEmergencyStopAttempt attempt = await context.PrinterEmergencyStopAttempts.SingleAsync();
            await service.FinishEmergencyStopAsync(printerId,
                new(id, attempt.Id, attempt.ConfigurationIdentity), PrinterEmergencyStopDelivery.Accepted, default);
            Assert.Equal(successor, (await context.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
        });
    }

    [Fact]
    public async Task EmergencyStopAsync_UnauthorizedActor_CannotFenceOrSend()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        authentication.Setup(service => service.HasPermissionAsync(userId, "queue", "cancel")).ReturnsAsync(false);
        await ChangeAsync(async (_, service) =>
        {
            PrinterControlException denied = await Assert.ThrowsAsync<PrinterControlException>(() =>
                service.PrepareEmergencyStopAsync(printerId, userId.ToString(), default));
            Assert.Equal(403, denied.Status);
            Assert.Equal(PrinterControlState.Queued, (await service.GetAsync(printerId, id, default)).State);
        });
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData(false, "false")]
    [InlineData(false, "cancelled")]
    [InlineData(false, "response_lost")]
    [InlineData(true, "false")]
    [InlineData(true, "cancelled")]
    [InlineData(true, "response_lost")]
    public async Task EmergencyStopAsync_AmbiguousHttpDelivery_RemainsStickyAfterMotionQuiesces(bool running, string outcome)
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        if (running)
        {
            await ChangeAsync(async (_, service) =>
            {
                await service.ClaimAsync(id, owner, default);
                await service.CommitSendAsync(id, owner, default);
            });
        }

        var backend = new Mock<IBackendClient>();
        var emergency = backend.As<ISupportsEmergencyStop>();
        emergency.Setup(service => service.EmergencyStopAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .Returns(() => outcome switch
            {
                "cancelled" => Task.FromCanceled<bool>(new CancellationToken(true)),
                "response_lost" => Task.FromException<bool>(new HttpRequestException("Response lost")),
                _ => Task.FromResult(false),
            });
        var printers = new Mock<IPrintersService>();
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            PrintersService concrete = CreateConcreteEmergencyService(scope.ServiceProvider, backend.Object);
            printers.Setup(service => service.EmergencyStopAsync(printerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid printer, string identity, CancellationToken token) => await concrete.EmergencyStopAsync(printer, identity, token));
            var response = await CreateEmergencyController(scope.ServiceProvider, printers).EmergencyStopAsync(printerId, default);
            Assert.Equal(503, Assert.IsType<ObjectResult>(response.Result).StatusCode);
        }

        await ChangeAsync(async (db, service) =>
        {
            await service.MaintainOwnerAsync(id, owner, true, default);
            PrinterControlOperationDto current = await service.GetAsync(printerId, id, default);
            Assert.Equal(PrinterSenderIsolation.ExternalVerificationRequired, current.SenderIsolation);
            Assert.True(current.BarrierHeld);
            Assert.Equal(PrinterEmergencyStopDelivery.Unknown, (await db.PrinterEmergencyStopAttempts.SingleAsync()).Delivery);
            var blocked = await Assert.ThrowsAsync<PrinterControlException>(() => service.CompleteRecoveryAsync(
                printerId, id, Quote(current.RowVersion), userId.ToString(), Evidence() with { SenderIsolation = "ServiceConfirmed" }, default));
            Assert.Equal(409, blocked.Status);
            await service.SetOutcomeAsync(id, owner, true, null, default);
            Assert.Equal(PrinterControlState.Recovering, (await service.GetAsync(printerId, id, default)).State);
        });
        printers.Verify(service => service.EmergencyStopAsync(printerId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        emergency.Verify(service => service.EmergencyStopAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmergencyStopAsync_CrashBeforeHttpWrite_NewServiceAllowsExplicitSecondAttempt(bool committed)
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        PrinterEmergencyStopLease abandoned = null!;
        await ChangeAsync(async (_, service) =>
        {
            abandoned = (await service.PrepareEmergencyStopAsync(printerId, userId.ToString(), default))!;
            if (committed)
            {
                Assert.True(await service.CommitEmergencyStopSendAsync(printerId, abandoned, userId.ToString(), default));
            }
        });
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.EmergencyStopAsync(printerId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await using (AsyncServiceScope restarted = provider.CreateAsyncScope())
        {
            var response = await CreateEmergencyController(restarted.ServiceProvider, printers).EmergencyStopAsync(printerId, default);
            Assert.Equal(200, Assert.IsType<ObjectResult>(response.Result).StatusCode);
        }

        await ChangeAsync(async (db, service) =>
        {
            Assert.Equal(2, await db.PrinterEmergencyStopAttempts.CountAsync());
            Assert.Equal(PrinterEmergencyStopDelivery.Pending,
                (await db.PrinterEmergencyStopAttempts.SingleAsync(a => a.Id == abandoned.AttemptId)).Delivery);
            PrinterControlOperationDto current = await service.GetAsync(printerId, id, default);
            Assert.Equal(PrinterSenderIsolation.ExternalVerificationRequired, current.SenderIsolation);
            await Assert.ThrowsAsync<PrinterControlException>(() => service.CompleteRecoveryAsync(
                printerId, id, Quote(current.RowVersion), userId.ToString(), Evidence() with { SenderIsolation = "ServiceConfirmed" }, default));
            await service.CompleteRecoveryAsync(printerId, id, Quote(current.RowVersion), userId.ToString(), Evidence(), default);
        });
        Guid successor = Guid.NewGuid();
        await AdmitAsync(successor);
        await ChangeAsync(async (db, service) =>
        {
            Assert.False(await service.CommitEmergencyStopSendAsync(printerId, abandoned, userId.ToString(), default));
            await service.FinishEmergencyStopAsync(printerId, abandoned, PrinterEmergencyStopDelivery.Unknown, default);
            Assert.Equal(PrinterEmergencyStopDelivery.ExternallyIsolated,
                (await db.PrinterEmergencyStopAttempts.SingleAsync(a => a.Id == abandoned.AttemptId)).Delivery);
            Assert.Equal(successor, (await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
        });
        printers.Verify(service => service.EmergencyStopAsync(printerId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EmergencyStopAsync_ConcurrentAcceptedAndAmbiguousAttempts_CannotClearEachOther()
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (_, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            await service.CommitSendAsync(id, owner, default);
        });
        var firstSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPrinter = new Mock<IPrintersService>();
        firstPrinter.Setup(service => service.EmergencyStopAsync(printerId, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(async () =>
        {
            firstSent.SetResult();
            return await firstResult.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });
        await using AsyncServiceScope firstScope = provider.CreateAsyncScope();
        Task<ActionResult<CommandResult>> first = CreateEmergencyController(firstScope.ServiceProvider, firstPrinter).EmergencyStopAsync(printerId, default);
        await firstSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var secondPrinter = new Mock<IPrintersService>();
        secondPrinter.Setup(service => service.EmergencyStopAsync(printerId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await using (AsyncServiceScope secondScope = provider.CreateAsyncScope())
        {
            Assert.Equal(200, Assert.IsType<ObjectResult>(
                (await CreateEmergencyController(secondScope.ServiceProvider, secondPrinter).EmergencyStopAsync(printerId, default)).Result).StatusCode);
        }

        await ChangeAsync(async (db, service) =>
        {
            await service.MaintainOwnerAsync(id, owner, true, default);
            Assert.Equal(1, await db.PrinterEmergencyStopAttempts.CountAsync(a => a.Delivery == PrinterEmergencyStopDelivery.Pending));
            Assert.Equal(1, await db.PrinterEmergencyStopAttempts.CountAsync(a => a.Delivery == PrinterEmergencyStopDelivery.Accepted));
            Assert.Equal(PrinterSenderIsolation.ExternalVerificationRequired, (await service.GetAsync(printerId, id, default)).SenderIsolation);
        });
        firstResult.SetResult(false);
        Assert.Equal(503, Assert.IsType<ObjectResult>((await first.WaitAsync(TimeSpan.FromSeconds(3))).Result).StatusCode);
        await ChangeAsync(async (db, service) =>
        {
            await service.MaintainOwnerAsync(id, owner, true, default);
            Assert.Equal(1, await db.PrinterEmergencyStopAttempts.CountAsync(a => a.Delivery == PrinterEmergencyStopDelivery.Unknown));
            Assert.Equal(PrinterSenderIsolation.ExternalVerificationRequired, (await service.GetAsync(printerId, id, default)).SenderIsolation);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmergencyStopAsync_AdmissionCasRace_ReacquiresCurrentFenceWithoutReplayingHttp(bool settles)
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (_, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            if (settles)
            {
                await service.CommitSendAsync(id, owner, default);
            }
        });
        bool raced = false;
        commands.ConflictNextOperationUpdate = true;
        authorization.Setup(a => a.CanActorAccessPrinterAsync(It.IsAny<string>(), printerId, It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (commands.ConflictCount > 0 && !raced)
                {
                    raced = true;
                    await ChangeAsync(async (_, service) =>
                    {
                        if (settles)
                        {
                            await service.SetOutcomeAsync(id, owner, true, null, default);
                        }
                        else
                        {
                            await service.CommitSendAsync(id, owner, default);
                        }
                    });
                }

                return true;
            });
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.EmergencyStopAsync(printerId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        printers.Setup(service => service.EmergencyStopAsync(printerId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var result = await CreateEmergencyController(scope.ServiceProvider, printers).EmergencyStopAsync(printerId, default);
        Assert.True(raced);
        if (settles)
        {
            Assert.True(result.Value?.Success);
        }
        else
        {
            Assert.Equal(200, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        }

        Assert.Equal(1, printers.Invocations.Count(call => call.Method.Name == nameof(IPrintersService.EmergencyStopAsync)));
        Assert.Equal(settles ? PrinterControlState.Succeeded : PrinterControlState.Recovering, (await GetAsync(id)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmergencyStopAsync_LegacyUncertainty_NewAcceptedStopNeverClearsOldSender(bool flag)
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (db, _) =>
        {
            PrinterControlOperation operation = await db.PrinterControlOperations.SingleAsync();
            operation.State = PrinterControlState.Recovering;
            operation.EmergencyStopInFlight = flag;
            operation.SenderIsolation = PrinterSenderIsolation.Confirmed;
            operation.FailureCode = flag ? "emergency_stop_requested" : "emergency_stop_outcome_unknown";
            await db.SaveChangesAsync();
        });
        await ChangeAsync(async (db, service) =>
        {
            PrinterEmergencyStopLease attempt = (await service.PrepareEmergencyStopAsync(printerId, userId.ToString(), default))!;
            Assert.True(await service.CommitEmergencyStopSendAsync(printerId, attempt, userId.ToString(), default));
            await service.FinishEmergencyStopAsync(printerId, attempt, PrinterEmergencyStopDelivery.Accepted, default);
            Assert.True((await db.PrinterControlOperations.SingleAsync()).EmergencyStopInFlight);
            PrinterControlOperationDto current = await service.GetAsync(printerId, id, default);
            Assert.Equal(PrinterSenderIsolation.ExternalVerificationRequired, current.SenderIsolation);
            await Assert.ThrowsAsync<PrinterControlException>(() => service.CompleteRecoveryAsync(
                printerId, id, Quote(current.RowVersion), userId.ToString(), Evidence() with { SenderIsolation = "ServiceConfirmed" }, default));
        });
    }

    [Fact]
    public async Task EmergencyStopAsync_ConfigChangesBeforeSend_RequiresNewFenceAndRecordsProvenNotSent()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        PrinterEmergencyStopLease attempt = null!;
        await ChangeAsync(async (_, service) =>
            attempt = (await service.PrepareEmergencyStopAsync(printerId, userId.ToString(), default))!);
        await ChangeAsync(async (db, _) =>
        {
            (await db.Printers.SingleAsync()).ServerUrl = "http://changed.invalid";
            await db.SaveChangesAsync();
        });
        await ChangeAsync(async (db, service) =>
        {
            Assert.False(await service.CommitEmergencyStopSendAsync(printerId, attempt, userId.ToString(), default));
            await service.FinishEmergencyStopAsync(printerId, attempt, PrinterEmergencyStopDelivery.NotSent, default);
            PrinterEmergencyStopAttempt stored = await db.PrinterEmergencyStopAttempts.SingleAsync();
            Assert.Null(stored.SendCommittedAtUtc);
            Assert.Equal(PrinterEmergencyStopDelivery.NotSent, stored.Delivery);
            Assert.Equal(PrinterSenderIsolation.Confirmed, (await service.GetAsync(printerId, id, default)).SenderIsolation);
        });
        Assert.Equal(0, channel.SendCount);
    }

    [Fact]
    public async Task EmergencyStopAsync_SendCommitCasRace_RetainsOtherAttemptAmbiguity()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        PrinterEmergencyStopLease first = null!;
        PrinterEmergencyStopLease other = null!;
        await ChangeAsync(async (_, service) =>
        {
            first = (await service.PrepareEmergencyStopAsync(printerId, userId.ToString(), default))!;
            other = (await service.PrepareEmergencyStopAsync(printerId, userId.ToString(), default))!;
            Assert.True(await service.CommitEmergencyStopSendAsync(printerId, other, userId.ToString(), default));
        });
        bool raced = false;
        commands.ConflictNextOperationUpdate = true;
        authorization.Setup(a => a.CanActorAccessPrinterAsync(It.IsAny<string>(), printerId, It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (commands.ConflictCount > 0 && !raced)
                {
                    raced = true;
                    await ChangeAsync(async (_, service) =>
                        await service.FinishEmergencyStopAsync(printerId, other, PrinterEmergencyStopDelivery.Unknown, default));
                }

                return true;
            });
        await ChangeAsync(async (db, service) =>
        {
            Assert.True(await service.CommitEmergencyStopSendAsync(printerId, first, userId.ToString(), default));
            await service.FinishEmergencyStopAsync(printerId, first, PrinterEmergencyStopDelivery.Accepted, default);
            Assert.Equal(PrinterEmergencyStopDelivery.Unknown,
                (await db.PrinterEmergencyStopAttempts.SingleAsync(a => a.Id == other.AttemptId)).Delivery);
            Assert.Equal(PrinterSenderIsolation.ExternalVerificationRequired, (await service.GetAsync(printerId, id, default)).SenderIsolation);
        });
        Assert.True(raced);
    }

    [Fact]
    public async Task EmergencyStopAsync_ConcreteServiceRejectsChangedConfigurationWithoutBackendInvocation()
    {
        var backend = new Mock<IBackendClient>();
        var emergency = backend.As<ISupportsEmergencyStop>();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        PrintersService service = CreateConcreteEmergencyService(scope.ServiceProvider, backend.Object);
        Assert.False(await service.EmergencyStopAsync(printerId, "not-the-current-configuration", default));
        emergency.Verify(e => e.EmergencyStopAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmergencyStopAsync_EvidenceWriteFailsAfterHttp_NeverClaimsNotSent(bool accepted)
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.EmergencyStopAsync(printerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                commands.FailNextOperationUpdate = true;
                return Task.FromResult(accepted);
            });
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var response = Assert.IsType<ObjectResult>(
                (await CreateEmergencyController(scope.ServiceProvider, printers).EmergencyStopAsync(printerId, default)).Result);
            Assert.Equal(503, response.StatusCode);
            Assert.Equal("emergency_stop_outcome_unknown", Assert.IsType<ProblemDetails>(response.Value).Extensions["code"]);
        }

        await ChangeAsync(async (db, service) =>
        {
            Assert.Equal(PrinterEmergencyStopDelivery.Pending, (await db.PrinterEmergencyStopAttempts.SingleAsync()).Delivery);
            Assert.Equal(PrinterSenderIsolation.ExternalVerificationRequired, (await service.GetAsync(printerId, id, default)).SenderIsolation);
        });
        printers.Verify(service => service.EmergencyStopAsync(printerId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CurrentAsync_ViewOnlyLegacyRead_DoesNotImportOrSend_WorkerImportsInstead()
    {
        Guid id = Guid.NewGuid();
        authentication.Setup(a => a.HasPermissionAsync(userId, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        await ChangeAsync(async (db, _) =>
        {
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            barrier.PhysicalControlCommandId = id;
            barrier.PhysicalControlOperation = "home";
            barrier.PhysicalControlRequiresReconciliation = true;
            await db.SaveChangesAsync();
        });
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            var current = Assert.IsType<PrinterControlCurrentDto>(
                Assert.IsType<OkObjectResult>(await CreateMotionController(scope.ServiceProvider).CurrentAsync(printerId, default)).Value);
            Assert.True(current.PhysicalControl.BarrierHeld);
            Assert.Null(current.Operation);
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.PrinterControlOperations.CountAsync());
            Assert.Equal(0, await db.QueueOperationAudits.CountAsync());
            Assert.Equal(0, await db.QueueDispatchOutbox.CountAsync());
        }

        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PrinterControlOperationWorker>.Instance);
        await worker.TickAsync(default);
        Assert.Equal(PrinterControlState.Unknown, (await GetAsync(id)).State);
        Assert.Equal(0, channel.SendCount);
        await ChangeAsync(async (db, _) =>
        {
            Assert.Equal(1, await db.QueueOperationAudits.CountAsync());
            Assert.Equal(1, await db.QueueDispatchOutbox.CountAsync());
        });
    }

    [Fact]
    public async Task AdmitAsync_FirstControl_CreatesDispatchStateAtomically()
    {
        await ChangeAsync(async (db, _) => await db.PrinterDispatchStates.ExecuteDeleteAsync());
        PrinterControlOperationDto admitted = await AdmitAsync(Guid.NewGuid());
        Assert.True(admitted.BarrierHeld);
        await ChangeAsync(async (db, _) =>
        {
            Assert.Equal(admitted.OperationId, (await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
            Assert.Equal(1, await db.QueueDispatchOutbox.CountAsync());
        });
    }

    [Fact]
    public async Task AdmitAsync_RevokedActor_CannotReplayExistingOperation()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        authentication.Setup(a => a.HasPermissionAsync(userId, "queue", "start")).ReturnsAsync(false);
        PrinterControlException error = await Assert.ThrowsAsync<PrinterControlException>(() => AdmitAsync(id));
        Assert.Equal(403, error.Status);
    }

    [Fact]
    public async Task ClaimAsync_TwoWorkers_OnlyOneOwnerCanCommitSend()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        Guid firstOwner = Guid.NewGuid();
        Guid secondOwner = Guid.NewGuid();
        await using AsyncServiceScope first = provider.CreateAsyncScope();
        await using AsyncServiceScope second = provider.CreateAsyncScope();
        PrinterControlOperationService one = first.ServiceProvider.GetRequiredService<PrinterControlOperationService>();
        PrinterControlOperationService two = second.ServiceProvider.GetRequiredService<PrinterControlOperationService>();
        // Both replicas read the same unclaimed revision; the loser's save must fail CAS.
        await second.ServiceProvider.GetRequiredService<AppDbContext>().PrinterControlOperations.SingleAsync(o => o.Id == id);
        Assert.NotNull(await one.ClaimAsync(id, firstOwner, default));
        Assert.Null(await two.ClaimAsync(id, secondOwner, default));
        Assert.False(await two.CommitSendAsync(id, secondOwner, default));
        Assert.True(await one.CommitSendAsync(id, firstOwner, default));
        Assert.False(await one.CommitSendAsync(id, firstOwner, default));
    }

    [Fact]
    public async Task AdmitAsync_ConcurrentDuplicate_StoresExactlyOnePhysicalIntent()
    {
        Guid id = Guid.NewGuid();
        PrinterControlOperationDto[] results = await Task.WhenAll(Task.Run(() => AdmitAsync(id)), Task.Run(() => AdmitAsync(id)));
        Assert.All(results, result => Assert.Equal(id, result.OperationId));
        await ChangeAsync(async (db, _) =>
        {
            Assert.Equal(1, await db.PrinterControlOperations.CountAsync());
            Assert.Equal(1, await db.QueueOperationAudits.CountAsync());
            Assert.Equal(1, await db.QueueDispatchOutbox.CountAsync());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitResubmission_LostBeforeOrAfterAdmission_ExecutesExactlyOnce(bool originallyAdmitted)
    {
        Guid savedId = Guid.NewGuid();
        if (originallyAdmitted)
        {
            // The server admitted the intent, but its HTTP response never reached the client.
            await AdmitAsync(savedId);
        }
        else
        {
            PrinterControlException missing = await Assert.ThrowsAsync<PrinterControlException>(() => GetAsync(savedId));
            Assert.Equal(404, missing.Status);
        }

        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PrinterControlOperationWorker>.Instance);
        await worker.StartAsync(default);
        if (originallyAdmitted)
        {
            await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // This models a NEW explicit user confirmation, never an automatic transport retry.
        PrinterControlOperationDto resumed = await AdmitAsync(savedId);
        Assert.Equal(savedId, resumed.OperationId);
        Assert.Equal(PrinterControlKind.HomeAll, resumed.Kind);
        await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        channel.Completion.SetResult();
        await WaitForStateAsync(savedId, PrinterControlState.Succeeded);
        PrinterControlOperationDto terminalReplay = await AdmitAsync(savedId);
        Assert.Equal(PrinterControlState.Succeeded, terminalReplay.State);
        Assert.Equal(PrinterControlEvidence.MotionQueueDrained, terminalReplay.CompletionEvidence);
        await worker.StopAsync(default);
        Assert.Equal(1, channel.SendCount);
        await ChangeAsync(async (db, _) =>
        {
            Assert.Equal(1, await db.PrinterControlOperations.CountAsync());
            Assert.Equal(3, await db.QueueDispatchOutbox.CountAsync());
        });
    }

    [Fact]
    public async Task WorkerAsync_GracefulShutdown_DrainsAlreadySentOperation()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        var logger = new WorkerLogger();
        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(), logger);
        await worker.StartAsync(default);
        await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task shutdown = worker.StopAsync(grace.Token);
        Assert.False(shutdown.IsCompleted);
        channel.Completion.SetResult();
        await shutdown;
        Assert.Equal(PrinterControlState.Succeeded, (await GetAsync(id)).State);
        Assert.Equal(1, channel.SendCount);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task WorkerAsync_ScanFailure_LogsOnlyExceptionTypeAndRetainsUnsentBarrier()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        var scopes = new Mock<IServiceScopeFactory>();
        scopes.Setup(factory => factory.CreateScope()).Throws(new InvalidOperationException(SensitiveFailureDetail));
        var logger = new WorkerLogger();
        using var worker = new PrinterControlOperationWorker(scopes.Object, logger);
        await worker.StartAsync(default);
        await logger.FirstWarning.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        WorkerLog warning = Assert.Single(logger.Entries);
        Assert.Equal(nameof(InvalidOperationException), LogFields(warning)["ExceptionType"]);
        Assert.Contains("persisted barriers remain held", warning.Message, StringComparison.Ordinal);
        AssertSafeLogs(logger, SensitiveFailureDetail);
        PrinterControlOperationDto operation = await GetAsync(id);
        Assert.Equal(PrinterControlState.Queued, operation.State);
        Assert.True(operation.BarrierHeld);
        Assert.Equal(0, channel.SendCount);
    }

    [Fact]
    public async Task WorkerAsync_OutcomeWriteFailure_LogsRetryContextWithoutReplayingMotion()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        var logger = new WorkerLogger();
        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(), logger);
        // Tick manually so only outcome persistence, not a concurrent scan, consumes the injected failure.
        await worker.TickAsync(default);
        await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        commands.FailNextOperationUpdate = true;
        channel.Completion.SetResult();
        await logger.FirstWarning.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForStateAsync(id, PrinterControlState.Succeeded);
        await worker.StopAsync(default);

        WorkerLog warning = Assert.Single(logger.Entries);
        Dictionary<string, object?> fields = LogFields(warning);
        Assert.Equal(id, fields["OperationId"]);
        Assert.Equal(nameof(DbUpdateException), fields["ExceptionType"]);
        Assert.Equal(1, fields["Attempt"]);
        Assert.Equal(12, fields["MaxAttempts"]);
        AssertSafeLogs(logger, "Injected evidence persistence failure");
        PrinterControlOperationDto operation = await GetAsync(id);
        Assert.Equal(PrinterControlEvidence.MotionQueueDrained, operation.CompletionEvidence);
        Assert.False(operation.BarrierHeld);
        Assert.Equal(1, channel.SendCount);
    }

    [Theory]
    [InlineData(PrinterControlKind.HomeXY)]
    [InlineData(PrinterControlKind.HomeZ)]
    [InlineData(PrinterControlKind.Jog)]
    [InlineData(PrinterControlKind.MoveTo)]
    public async Task WorkerAsync_AllMotionKinds_DrainAndSettle(PrinterControlKind kind)
    {
        Guid id = Guid.NewGuid();
        await ChangeAsync(async (_, service) => await service.AdmitAsync(printerId, id, userId.ToString(),
            new(kind, X: kind is PrinterControlKind.Jog or PrinterControlKind.MoveTo ? 1.5 : null,
                Y: kind == PrinterControlKind.MoveTo ? 2 : null, Z: kind == PrinterControlKind.MoveTo ? 10 : null), default));
        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PrinterControlOperationWorker>.Instance);
        await worker.StartAsync(default);
        await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.EndsWith("\nM400", channel.Script, StringComparison.Ordinal);
        channel.Completion.SetResult();
        await WaitForStateAsync(id, PrinterControlState.Succeeded);
        await worker.StopAsync(default);
        Assert.Equal(1, channel.SendCount);
        safety.Verify(guard => guard.ValidateObservedMoveAsync(printerId,
            It.IsAny<PrinterSafetyMoveRequest>(), It.IsAny<PrinterStatusDto>(), It.IsAny<CancellationToken>()),
            kind is PrinterControlKind.MoveTo or PrinterControlKind.Jog ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkerAsync_BusyOrUnsafe_ReleasesOnlyUnsentOperation(bool busy)
    {
        Guid id = Guid.NewGuid();
        channel.Idle = !busy;
        safety.Setup(guard => guard.ValidateObservedMoveAsync(printerId,
            It.IsAny<PrinterSafetyMoveRequest>(), It.IsAny<PrinterStatusDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterSafetyValidationResult.Reject(409, "axes_not_homed", "Axes are not homed."));
        await ChangeAsync(async (_, service) => await service.AdmitAsync(printerId, id, userId.ToString(),
            new(PrinterControlKind.MoveTo, X: 1, Y: 2, Z: 10), default));
        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PrinterControlOperationWorker>.Instance);
        await worker.StartAsync(default);
        await WaitForStateAsync(id, PrinterControlState.Failed);
        await worker.StopAsync(default);
        PrinterControlOperationDto result = await GetAsync(id);
        Assert.False(result.BarrierHeld);
        Assert.Equal(PrinterControlEvidence.NotSent, result.CompletionEvidence);
        Assert.Equal(busy ? "printer_busy" : "axes_not_homed", result.Failure?.Code);
        Assert.Equal(0, channel.SendCount);
    }

    [Fact]
    public async Task WorkerAsync_AlreadyUnknownSender_StillAcknowledgesLaterRecovery()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        var logger = new WorkerLogger();
        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(), logger);
        await worker.StartAsync(default);
        await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var transportFailure = new IOException(SensitiveFailureDetail, new InvalidOperationException(SensitiveFailureDetail));
        transportFailure.Data["request"] = SensitiveFailureDetail;
        channel.Completion.SetException(transportFailure);
        await WaitForStateAsync(id, PrinterControlState.Unknown);
        PrinterControlOperationDto unknown = await GetAsync(id);
        Assert.True(unknown.BarrierHeld);
        Assert.Equal("backend_outcome_unknown", unknown.Failure?.Code);
        WorkerLog warning = Assert.Single(logger.Entries);
        Assert.Equal(nameof(IOException), LogFields(warning)["ExceptionType"]);
        Assert.Equal(id, LogFields(warning)["OperationId"]);
        AssertSafeLogs(logger, SensitiveFailureDetail);
        await ChangeAsync(async (_, service) =>
            await service.BeginRecoveryAsync(printerId, id, Quote(unknown.RowVersion), userId.ToString(), default));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await GetAsync(id)).SenderIsolation != PrinterSenderIsolation.Confirmed)
        {
            await Task.Delay(20, timeout.Token);
        }
        await worker.StopAsync(default);
        PrinterControlOperationDto isolated = await GetAsync(id);
        await ChangeAsync(async (db, service) =>
        {
            (await db.PrinterControlOperations.SingleAsync()).OwnerHeartbeatAtUtc = DateTime.UtcNow.AddMinutes(-2);
            await db.SaveChangesAsync();
            await service.ReconcileOrphansAsync(default);
        });
        PrinterControlOperationDto retained = await GetAsync(id);
        Assert.Equal(isolated.SenderIsolation, retained.SenderIsolation);
        await ChangeAsync(async (_, service) =>
        {
            PrinterControlOperationDto recovered = await service.CompleteRecoveryAsync(printerId, id, Quote(retained.RowVersion),
                userId.ToString(), Evidence() with { SenderIsolation = "ServiceConfirmed" }, default);
            Assert.Equal(PrinterControlState.Recovered, recovered.State);
        });
        Assert.Equal(1, channel.SendCount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"kind\":0}")]
    [InlineData("{\"kind\":\"0\"}")]
    public void MotionRequest_RequiresExplicitNamedKind(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PrinterControlRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task MaintainOwnerAsync_Heartbeat_DoesNotInvalidatePublicRevision()
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (_, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            await service.CommitSendAsync(id, owner, default);
        });
        PrinterControlOperationDto before = await GetAsync(id);
        await ChangeAsync(async (_, service) => await service.MaintainOwnerAsync(id, owner, false, default));
        Assert.Equal(before, await GetAsync(id));
    }

    [Fact]
    public async Task CommitSendAsync_RevokedPermission_DoesNotCommitPhysicalSend()
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (_, service) => await service.ClaimAsync(id, owner, default));
        authentication.Setup(a => a.HasPermissionAsync(userId, "queue", "start")).ReturnsAsync(false);
        await ChangeAsync(async (db, service) =>
        {
            await Assert.ThrowsAsync<PrinterControlException>(() => service.CommitSendAsync(id, owner, default));
            Assert.Null((await db.PrinterControlOperations.SingleAsync()).SendCommittedAtUtc);
        });
    }

    [Fact]
    public async Task CommitSendAsync_ChangedConfiguration_DoesNotCommitPhysicalSend()
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (db, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            (await db.Printers.SingleAsync()).BackendPort++;
            await db.SaveChangesAsync();
            PrinterControlException error = await Assert.ThrowsAsync<PrinterControlException>(() => service.CommitSendAsync(id, owner, default));
            Assert.Equal("printer_configuration_changed", error.Code);
            Assert.Null((await db.PrinterControlOperations.SingleAsync()).SendCommittedAtUtc);
        });
    }

    [Fact]
    public async Task DeletePrinterAsync_UnresolvedOperation_PreservesPrinterAndBarrier()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (db, _) =>
        {
            var repository = new EfPrintersRepository(db, Mock.Of<ISensitiveDataProtector>());
            PrinterControlException error = await Assert.ThrowsAsync<PrinterControlException>(() =>
                repository.RemoveAsync(new Printer { Id = printerId }, default));
            Assert.Equal("physical_control_barrier", error.Code);
            Assert.True(await db.Printers.AnyAsync(p => p.Id == printerId));
            Assert.Equal(id, (await db.PrinterDispatchStates.AsNoTracking().SingleAsync()).PhysicalControlCommandId);
        });
    }

    [Fact]
    public async Task ProjectAsync_OtherAdapterLegacyBarrier_IsNeverErasedByCapabilities()
    {
        await ChangeAsync(async (db, _) =>
        {
            (await db.Printers.SingleAsync()).Backend = (int)PrinterBackend.OctoPrint;
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            barrier.PhysicalControlCommandId = Guid.NewGuid();
            barrier.PhysicalControlRequiresReconciliation = true;
            await db.SaveChangesAsync();
            PrinterPhysicalControlDto projection = (await PrinterControlOperationService.ProjectAsync(db, [printerId], default))[printerId];
            Assert.Empty(projection.SupportedOperations);
            Assert.True(projection.BarrierHeld);
            Assert.True(projection.RequiresRecovery);
            Assert.Null(projection.OperationId);
        });
    }

    [Fact]
    public async Task ClaimAsync_ExpiredUnsentOwner_OldOwnerCannotSend()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        Guid owner = Guid.NewGuid();
        await ChangeAsync(async (db, service) =>
        {
            Assert.NotNull(await service.ClaimAsync(id, owner, default));
            (await db.PrinterControlOperations.SingleAsync()).OwnerHeartbeatAtUtc = DateTime.UtcNow.AddMinutes(-2);
            await db.SaveChangesAsync();
        });
        await ChangeAsync(async (_, service) =>
        {
            Assert.NotNull(await service.ClaimAsync(id, Guid.NewGuid(), default));
            Assert.False(await service.CommitSendAsync(id, owner, default));
        });
    }

    [Fact]
    public async Task RecoveryAsync_SentCrash_RequiresEvidenceAndFencesLateCompletion()
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (db, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            Assert.True(await service.CommitSendAsync(id, owner, default));
            (await db.PrinterControlOperations.SingleAsync()).OwnerHeartbeatAtUtc = DateTime.UtcNow.AddMinutes(-2);
            await db.SaveChangesAsync();
        });
        await ChangeAsync(async (_, service) => await service.ReconcileOrphansAsync(default));
        PrinterControlOperationDto unknown = await GetAsync(id);
        Assert.Equal(PrinterControlState.Unknown, unknown.State);
        Assert.True(unknown.BarrierHeld);
        await ChangeAsync(async (_, service) =>
        {
            Assert.Null(await service.ClaimAsync(id, Guid.NewGuid(), default));
            PrinterControlOperationDto recovering = await service.BeginRecoveryAsync(printerId, id, Quote(unknown.RowVersion), userId.ToString(), default);
            Assert.Equal(PrinterSenderIsolation.ExternalVerificationRequired, recovering.SenderIsolation);
            await Assert.ThrowsAsync<PrinterControlException>(() => service.CompleteRecoveryAsync(printerId, id, Quote(recovering.RowVersion),
                userId.ToString(), new("checked", "ServiceConfirmed", "socket closed", true, true, "inspected"), default));
        });
        PrinterControlOperationDto recovery = await GetAsync(id);
        await ChangeAsync(async (_, service) =>
        {
            PrinterControlException stale = await Assert.ThrowsAsync<PrinterControlException>(() => service.CompleteRecoveryAsync(
                printerId, id, Quote(unknown.RowVersion), userId.ToString(), Evidence(), default));
            Assert.Equal(412, stale.Status);
            PrinterControlOperationDto recovered = await service.CompleteRecoveryAsync(printerId, id, Quote(recovery.RowVersion), userId.ToString(), Evidence(), default);
            Assert.Equal(PrinterControlState.Recovered, recovered.State);
            Assert.False(recovered.BarrierHeld);
        });
        Guid successor = Guid.NewGuid();
        await AdmitAsync(successor);
        await ChangeAsync(async (db, service) =>
        {
            await service.SetOutcomeAsync(id, owner, true, null, default);
            Assert.Equal(successor, (await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
            Assert.NotNull((await db.PrinterControlOperations.SingleAsync(o => o.Id == id)).RecoveryEvidenceJson);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetOutcomeAsync_PreSendVersusAmbiguousWrite_ReleasesOnlyNotSent(bool sent)
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (_, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            if (sent)
            {
                await service.CommitSendAsync(id, owner, default);
            }
            await service.SetOutcomeAsync(id, owner, false, "test_failure", default);
        });
        PrinterControlOperationDto result = await GetAsync(id);
        Assert.Equal(sent, result.BarrierHeld);
        Assert.Equal(sent ? PrinterControlState.Unknown : PrinterControlState.Failed, result.State);
        Assert.Equal(sent ? PrinterControlEvidence.None : PrinterControlEvidence.NotSent, result.CompletionEvidence);
    }

    [Fact]
    public async Task SetOutcomeAsync_LateMatchingResponse_SettlesUnknownBeforeRecovery()
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (_, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            await service.CommitSendAsync(id, owner, default);
            await service.SetOutcomeAsync(id, owner, false, "sender_unavailable", default);
            await service.SetOutcomeAsync(id, owner, true, null, default);
        });
        PrinterControlOperationDto result = await GetAsync(id);
        Assert.Equal(PrinterControlState.Succeeded, result.State);
        Assert.Equal(PrinterControlEvidence.MotionQueueDrained, result.CompletionEvidence);
        Assert.False(result.BarrierHeld);
    }

    [Fact]
    public async Task ImportLegacyAsync_MissingTimestamp_ImportsUnknownButNotAttemptBoundControl()
    {
        Guid id = Guid.NewGuid();
        await ChangeAsync(async (db, service) =>
        {
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            barrier.PhysicalControlCommandId = id;
            barrier.PhysicalControlOperation = "home";
            barrier.PhysicalControlAttemptId = Guid.NewGuid();
            await db.SaveChangesAsync();
            await service.ImportLegacyAsync(printerId, default);
            Assert.Empty(await db.PrinterControlOperations.ToArrayAsync());
            barrier.PhysicalControlAttemptId = null;
            await db.SaveChangesAsync();
            await service.ImportLegacyAsync(printerId, default);
        });
        PrinterControlOperationDto result = await GetAsync(id);
        Assert.Equal(PrinterControlState.Unknown, result.State);
        Assert.Equal(PrinterSenderIsolation.ExternalVerificationRequired, result.SenderIsolation);
    }

    [Fact]
    public async Task WorkerAsync_RequestEnds_OperationRemainsRunningUntilExactCompletion()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PrinterControlOperationWorker>.Instance);
        await worker.StartAsync(default);
        await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.EndsWith("\nM400", channel.Script, StringComparison.Ordinal);
        Assert.Equal(PrinterControlState.Running, (await GetAsync(id)).State);
        channel.Completion.SetResult();
        await WaitForStateAsync(id, PrinterControlState.Succeeded);
        await worker.StopAsync(default);
        Assert.Equal(1, channel.SendCount);
    }

    [Fact]
    public async Task WorkerAsync_RecoveryRequest_JoinsTransportBeforeConfirmingIsolation()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PrinterControlOperationWorker>.Instance);
        await worker.StartAsync(default);
        await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Stop heartbeat races at the observation/POST boundary using the public CAS retry contract.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            PrinterControlOperationDto current = await GetAsync(id);
            try
            {
                await ChangeAsync(async (_, service) => await service.BeginRecoveryAsync(printerId, id, Quote(current.RowVersion), userId.ToString(), default));
                break;
            }
            catch (PrinterControlException exception) when (exception.Status == 412)
            {
            }
        }
        await channel.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);
        PrinterControlOperationDto isolated = await GetAsync(id);
        Assert.Equal(PrinterSenderIsolation.Confirmed, isolated.SenderIsolation);
        Assert.True(isolated.BarrierHeld);
    }

    [Fact]
    public async Task ControllerAsync_AdmissionAndRecoveryPreconditions_UseFixedWireContract()
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var controller = new PrinterControlOperationsController(scope.ServiceProvider.GetRequiredService<PrinterControlOperationService>(),
            authorization.Object, scope.ServiceProvider.GetRequiredService<AppDbContext>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
                },
            },
        };
        Guid id = Guid.NewGuid();
        var response = Assert.IsType<AcceptedResult>(await controller.SubmitAsync(printerId, new(PrinterControlKind.HomeAll), id.ToString(), default));
        PrinterControlOperationDto dto = Assert.IsType<PrinterControlOperationDto>(response.Value);
        Assert.Equal(Quote(dto.RowVersion), controller.Response.Headers.ETag);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        var missing = Assert.IsType<ObjectResult>(await controller.RecoverAsync(printerId, id, default));
        Assert.Equal(428, missing.StatusCode);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        }));
        Assert.Equal("Queued", json.RootElement.GetProperty("state").GetString());
        Assert.Equal("HomeAll", json.RootElement.GetProperty("kind").GetString());
        Assert.True(json.RootElement.GetProperty("barrierHeld").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("failure").ValueKind);

        Guid owner = Guid.NewGuid();
        await ChangeAsync(async (_, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            await service.CommitSendAsync(id, owner, default);
            await service.SetOutcomeAsync(id, owner, true, null, default);
        });
        var replay = Assert.IsType<OkObjectResult>(await controller.SubmitAsync(printerId, new(PrinterControlKind.HomeAll), id.ToString(), default));
        Assert.Equal(PrinterControlState.Succeeded, Assert.IsType<PrinterControlOperationDto>(replay.Value).State);
    }

    private async Task<PrinterControlOperationDto> AdmitAsync(Guid id)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PrinterControlOperationService>()
            .AdmitAsync(printerId, id, userId.ToString(), new(PrinterControlKind.HomeAll), default);
    }

    private PrinterControlOperationsController CreateMotionController(IServiceProvider services) =>
        new(services.GetRequiredService<PrinterControlOperationService>(), authorization.Object, services.GetRequiredService<AppDbContext>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
                },
            },
        };

    private async Task<PrinterControlOperationDto> GetAsync(Guid id)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PrinterControlOperationService>().GetAsync(printerId, id, default);
    }

    private PrintersController CreateEmergencyController(IServiceProvider services, Mock<IPrintersService> printers)
    {
        var real = new PrinterPhysicalActuationService(services.GetRequiredService<AppDbContext>(),
            services.GetRequiredService<IDbOutboxSequenceAllocator>(), authorization.Object, NullLogger<PrinterPhysicalActuationService>.Instance);
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        PrintersController controller = PrintersControllerControlGuardsTests.CreateController(printers,
            new Mock<IPrinterStatusCacheReader>(), out _, actuation: actuation,
            motionControl: services.GetRequiredService<PrinterControlOperationService>());
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));
        actuation.Setup(a => a.AcquireDirectAsync(printerId, userId.ToString(), "emergencystop", It.IsAny<CancellationToken>()))
            .Returns((Guid printer, string actor, string operation, CancellationToken token) => real.AcquireDirectAsync(printer, actor, operation, token));
        actuation.Setup(a => a.RevalidateDirectAsync(It.IsAny<PrinterActuationLease>(), It.IsAny<CancellationToken>()))
            .Returns((PrinterActuationLease lease, CancellationToken token) => real.RevalidateDirectAsync(lease, token));
        actuation.Setup(a => a.CompleteDirectAsync(It.IsAny<PrinterActuationLease>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((PrinterActuationLease lease, bool success, string detail, CancellationToken token) => real.CompleteDirectAsync(lease, success, detail, token));
        return controller;
    }

    private static PrintersService CreateConcreteEmergencyService(IServiceProvider services, IBackendClient backend)
    {
        AppDbContext db = services.GetRequiredService<AppDbContext>();
        var repository = new Mock<IPrintersRepository>();
        repository.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken ct) => db.Printers.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct));
        var unitOfWork = new Mock<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>();
        unitOfWork.Setup(u => u.Printers).Returns(repository.Object);
        var factory = new Mock<IBackendClientFactory>();
        factory.Setup(f => f.GetClient(PrinterBackend.Moonraker)).Returns(backend);
        return new PrintersService(
            unitOfWork.Object, db, factory.Object, Mock.Of<IBackendCapabilityFactory>(),
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            Mock.Of<IHttpClientFactory>(), NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(),
            Mock.Of<IMultiPrinterStatusCoordinator>(), Mock.Of<IPrinterStatusClientFactory>(),
            Mock.Of<IPrinterStatusCacheReader>(), Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(),
            Mock.Of<ISensitiveDataProtector>(), Mock.Of<Farm.Infrastructure.Services.Interfaces.ISpoolmanService>(),
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>(),
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageSpoolResolver>());
    }

    private async Task ChangeAsync(Func<AppDbContext, PrinterControlOperationService, Task> change)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        await change(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<PrinterControlOperationService>());
    }

    private async Task WaitForStateAsync(Guid id, PrinterControlState state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await GetAsync(id)).State != state)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static string Quote(string value) => $"\"{value}\"";
    private const string SensitiveFailureDetail = "sensitive-test-detail: endpoint, credential and request payload";

    private static Dictionary<string, object?> LogFields(WorkerLog entry) =>
        Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(entry.State).ToDictionary();

    private static void AssertSafeLogs(WorkerLogger logger, string sensitiveDetail)
    {
        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Null(entry.Exception);
            Assert.DoesNotContain(sensitiveDetail, entry.Message, StringComparison.Ordinal);
            Assert.All(LogFields(entry).Values, value =>
            {
                Assert.False(value is Exception);
                Assert.DoesNotContain(sensitiveDetail, value?.ToString() ?? string.Empty, StringComparison.Ordinal);
            });
        });
    }

    private sealed record WorkerLog(LogLevel Level, string Message, Exception? Exception, object? State);

    private sealed class WorkerLogger : ILogger<PrinterControlOperationWorker>
    {
        public ConcurrentQueue<WorkerLog> Entries { get; } = new();
        public TaskCompletionSource FirstWarning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue(new(logLevel, formatter(state, exception), exception, state));
            if (logLevel == LogLevel.Warning)
            {
                FirstWarning.TrySetResult();
            }
        }
    }

    private static PrinterControlRecoveryRequest Evidence() => new("Inspected", "ExternallyVerified",
        "Original sender process stopped and network isolated", true, true, "Queue cleared independently; printer physically inspected stationary");

    private sealed class FakeChannel : IMoonrakerMotionChannel
    {
        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendCount { get; private set; }
        public bool Idle { get; set; } = true;
        public string Script { get; private set; } = string.Empty;
        public Task<bool> IsIdleAsync(CancellationToken ct) => Task.FromResult(Idle);
        public PrinterStatusDto? MotionState { get; set; }
        public Task<PrinterStatusDto> ReadMotionStateAsync(Guid printerId, CancellationToken ct) =>
            Task.FromResult(MotionState ?? new PrinterStatusDto(printerId, true, "Idle", X: 50, Y: 50, Z: 10,
                SafetyTelemetry: PrinterSafetyTelemetryDto.Empty with
                {
                    HomedAxes = new(["x", "y", "z"], DateTime.UtcNow, 15, "test"),
                    CoordinateOriginOffsetMm = new(new(0, 0, 0), DateTime.UtcNow, 15, "test"),
                }));
        public async Task ExecuteAsync(Guid correlationId, string script, CancellationToken ct)
        {
            SendCount++;
            Script = script;
            Sent.SetResult();
            await Completion.Task.WaitAsync(ct);
        }
        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SnapshotCommands : DbCommandInterceptor
    {
        public bool Capture { get; set; }
        public bool ConflictNextOperationUpdate { get; set; }
        public bool FailNextOperationUpdate { get; set; }
        public int ConflictCount { get; private set; }
        public ConcurrentQueue<string> Statements { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (FailNextOperationUpdate && command.CommandText.Contains("UPDATE \"PrinterControlOperations\"", StringComparison.Ordinal))
            {
                FailNextOperationUpdate = false;
                throw new DbUpdateException("Injected evidence persistence failure");
            }

            if (ConflictNextOperationUpdate && command.CommandText.Contains("UPDATE \"PrinterControlOperations\"", StringComparison.Ordinal))
            {
                ConflictNextOperationUpdate = false;
                ConflictCount++;
                throw new DbUpdateConcurrencyException("Injected operation revision conflict");
            }

            if (Capture)
            {
                Statements.Enqueue(command.CommandText);
            }

            return ValueTask.FromResult(result);
        }
    }
}
