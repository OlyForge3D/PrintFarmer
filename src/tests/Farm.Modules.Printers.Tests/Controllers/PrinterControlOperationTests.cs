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
    private readonly Mock<IBackendClientFactory> clients = new();
    private readonly Mock<IBackendClient> client = new();
    private readonly Mock<ISupportsDurableMotion> motionCapability;
    private readonly SnapshotCommands commands = new();
    private ServiceProvider provider = null!;

    public PrinterControlOperationTests()
    {
        motionCapability = client.As<ISupportsDurableMotion>();
        motionCapability.SetupGet(m => m.SupportedMotionKinds).Returns(Enum.GetValues<PrinterControlKind>());
        motionCapability.Setup(m => m.ConnectAsync(It.IsAny<Printer>(), It.IsAny<CancellationToken>())).ReturnsAsync(channel);
        clients.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(client.Object);
        keepAlive = new SqliteConnection(connectionString);
        authorization.Setup(a => a.CanActorAccessPrinterAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        authorization.Setup(a => a.CanAccessPrinterAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<Guid>(), It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        authentication.Setup(a => a.HasPermissionAsync(userId, "queue", "start")).ReturnsAsync(true);
        safety.Setup(guard => guard.ValidateObservedManualMoveAsync(printerId,
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
        services.AddSingleton(clients.Object);
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
        Assert.Empty((await scope.ServiceProvider.GetRequiredService<PrinterControlOperationService>()
            .GetCurrentAsync(printerId, default)).PhysicalControl.SupportedOperations);
        Assert.Empty((await PrinterControlOperationService.ProjectAsync(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(), [printerId], null, default))[printerId].SupportedOperations);
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
    public async Task ControllerAsync_PartialMoveTo_AdmitsSparseIntent(double? x, double? y, double? z)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        PrinterControlOperationsController controller = CreateMotionController(scope.ServiceProvider);
        var result = Assert.IsType<AcceptedResult>(await controller.SubmitAsync(printerId,
            new(PrinterControlKind.MoveTo, x, y, z), Guid.NewGuid().ToString(), default));
        Assert.Equal(202, result.StatusCode);
        PrinterControlOperation operation = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PrinterControlOperations.SingleAsync();
        Assert.Equal(x, operation.X);
        Assert.Equal(y, operation.Y);
        Assert.Equal(z, operation.Z);
        Assert.Equal(0, channel.SendCount);
    }

    [Fact]
    public async Task WorkerAsync_AdvertisedNonMoonrakerBackend_UsesSemanticCapabilityAndExactCorrelation()
    {
        const int customBackend = 9001;
        clients.Setup(f => f.GetClient(customBackend)).Returns(client.Object);
        motionCapability.SetupGet(m => m.SupportedMotionKinds).Returns([PrinterControlKind.HomeAll]);
        await ChangeAsync(async (db, service) =>
        {
            (await db.Printers.SingleAsync()).Backend = customBackend;
            await db.SaveChangesAsync();
            Assert.Equal([PrinterControlKind.HomeAll], (await service.GetCurrentAsync(printerId, default)).PhysicalControl.SupportedOperations);
            Assert.Equal([PrinterControlKind.HomeAll],
                (await PrinterControlOperationService.ProjectAsync(db, [printerId], clients.Object, default))[printerId].SupportedOperations);
        });

        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PrinterControlOperationWorker>.Instance);
        await worker.StartAsync(default);
        await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new PrinterControlRequest(PrinterControlKind.HomeAll), channel.Request);
        Assert.Equal(id, channel.CorrelationId);
        motionCapability.Verify(m => m.ConnectAsync(It.Is<Printer>(p => p.Backend == customBackend &&
            p.BackendUrl == "http://test.invalid:7125"), It.IsAny<CancellationToken>()), Times.Once);
        channel.Completion.SetResult();
        await WaitForStateAsync(id, PrinterControlState.Succeeded);
        await worker.StopAsync(default);
        Assert.Equal(1, channel.SendCount);
    }

    [Fact]
    public async Task AdmitAsync_UnadvertisedKind_IsUnsupportedWithoutPersistingOrSending()
    {
        motionCapability.SetupGet(m => m.SupportedMotionKinds).Returns([PrinterControlKind.HomeZ]);
        await ChangeAsync(async (db, service) =>
        {
            PrinterControlException error = await Assert.ThrowsAsync<PrinterControlException>(() =>
                service.AdmitAsync(printerId, Guid.NewGuid(), userId.ToString(), new(PrinterControlKind.HomeAll), default));
            Assert.Equal("printer_operation_unsupported", error.Code);
            Assert.Empty(await db.PrinterControlOperations.ToArrayAsync());
        });
        Assert.Equal(0, channel.SendCount);
    }

    [Fact]
    public async Task AdmitAsync_UnresolvablePlugin_ReportsUnsupportedAndProjectsNoKinds()
    {
        clients.Setup(f => f.GetClient((int)PrinterBackend.Moonraker))
            .Throws(new InvalidOperationException("Plugin was not registered."));
        await ChangeAsync(async (db, service) =>
        {
            PrinterControlException error = await Assert.ThrowsAsync<PrinterControlException>(() =>
                service.AdmitAsync(printerId, Guid.NewGuid(), userId.ToString(), new(PrinterControlKind.HomeAll), default));
            Assert.Equal("printer_operation_unsupported", error.Code);
            Assert.Empty((await service.GetCurrentAsync(printerId, default)).PhysicalControl.SupportedOperations);
            Assert.Empty((await PrinterControlOperationService.ProjectAsync(db, [printerId], clients.Object, default))[printerId].SupportedOperations);
            Assert.Empty(await db.PrinterControlOperations.ToArrayAsync());
        });
        Assert.Equal(0, channel.SendCount);
    }

    [Fact]
    public async Task CommitSendAsync_CapabilityWithdrawnAfterAdmission_DoesNotCommitSend()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (db, service) =>
        {
            Guid owner = Guid.NewGuid();
            Assert.NotNull(await service.ClaimAsync(id, owner, default));
            motionCapability.SetupGet(m => m.SupportedMotionKinds).Returns([]);
            PrinterControlException error = await Assert.ThrowsAsync<PrinterControlException>(() =>
                service.CommitSendAsync(id, owner, default));
            Assert.Equal("printer_operation_unsupported", error.Code);
            Assert.Null((await db.PrinterControlOperations.AsNoTracking().SingleAsync()).SendCommittedAtUtc);
        });
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData("oversized", false, PrinterControlKind.Jog)]
    [InlineData("oversized", false, PrinterControlKind.MoveTo)]
    [InlineData("outside", false, PrinterControlKind.Jog)]
    [InlineData("outside", false, PrinterControlKind.MoveTo)]
    [InlineData("unhomed", false, PrinterControlKind.Jog)]
    [InlineData("unhomed", false, PrinterControlKind.MoveTo)]
    [InlineData("stale", false, PrinterControlKind.Jog)]
    [InlineData("stale", false, PrinterControlKind.MoveTo)]
    [InlineData("stale_frame", false, PrinterControlKind.Jog)]
    [InlineData("stale_frame", false, PrinterControlKind.MoveTo)]
    [InlineData("offset_target", false, PrinterControlKind.Jog)]
    [InlineData("offset_target", false, PrinterControlKind.MoveTo)]
    [InlineData("missing_position", false, PrinterControlKind.Jog)]
    [InlineData("missing_position", false, PrinterControlKind.MoveTo)]
    [InlineData("outside_current", false, PrinterControlKind.Jog)]
    [InlineData("outside_current", false, PrinterControlKind.MoveTo)]
    [InlineData("single_axis", true, PrinterControlKind.Jog)]
    [InlineData("single_axis", true, PrinterControlKind.MoveTo)]
    [InlineData("z_only", true, PrinterControlKind.Jog)]
    [InlineData("z_only", true, PrinterControlKind.MoveTo)]
    [InlineData("multi_axis", true, PrinterControlKind.Jog)]
    [InlineData("multi_axis", true, PrinterControlKind.MoveTo)]
    public async Task ControllerAndWorkerAsync_ManualMotion_ValidatesFreshObservedTargetBeforeAnySend(string scenario, bool allowed, PrinterControlKind kind)
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
                new(VerifiedSafetyFactState.Unknown, null, "moonraker:no authoritative clearance source", now)),
        };
        var capabilities = new Mock<IPrinterBackendCapabilitiesService>();
        capabilities.Setup(service => service.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterBackendCapabilitiesDto(printerId, "test", PrinterBackend.Moonraker) { VerifiedSafety = verified });
        var cache = new Mock<IPrinterStatusCacheReader>(MockBehavior.Strict);
        var guard = new PrinterSafetyGuard(capabilities.Object, cache.Object, TimeProvider.System);
        safety.Setup(service => service.ValidateObservedManualMoveAsync(printerId, It.IsAny<PrinterSafetyMoveRequest>(),
            It.IsAny<PrinterStatusDto>(), It.IsAny<CancellationToken>()))
            .Returns((Guid printer, PrinterSafetyMoveRequest target, PrinterStatusDto snapshot, CancellationToken token) =>
                guard.ValidateObservedManualMoveAsync(printer, target, snapshot, token));
        double x = scenario switch
        {
            "oversized" => double.MaxValue,
            "outside" => kind == PrinterControlKind.Jog ? 151 : 201,
            "outside_current" => kind == PrinterControlKind.Jog ? -double.MaxValue : 50,
            _ => kind == PrinterControlKind.Jog ? 1 : 51,
        };
        Guid id = Guid.NewGuid();
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            Assert.IsType<AcceptedResult>(await CreateMotionController(scope.ServiceProvider).SubmitAsync(printerId,
                new(kind, scenario == "z_only" ? null : x, scenario == "multi_axis" ? 2 : null, scenario is "multi_axis" or "z_only" ? 3 : null),
                id.ToString(), default));
        }

        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PrinterControlOperationWorker>.Instance);
        await worker.StartAsync(default);
        if (allowed)
        {
            await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new PrinterControlRequest(kind, scenario == "z_only" ? null : x,
                scenario == "multi_axis" ? 2 : null, scenario is "multi_axis" or "z_only" ? 3 : null), channel.Request);
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
                PrinterPhysicalControlDto projection = (await PrinterControlOperationService.ProjectAsync(db, [printerId], clients.Object, default))[printerId];
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
                    PrinterPhysicalControlDto projection = (await PrinterControlOperationService.ProjectAsync(db, [printerId], clients.Object, default))[printerId];
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
    [InlineData(PrinterControlState.Recovering)]
    public async Task EmergencyStopAsync_DurableMotion_IsOutOfBandAndCannotReleaseOrSettleSuccessor(PrinterControlState state)
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (db, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            await service.CommitSendAsync(id, owner, default);
            if (state == PrinterControlState.Recovering)
            {
                (await db.PrinterControlOperations.SingleAsync()).State = state;
                await db.SaveChangesAsync();
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
            Assert.True(during.BarrierHeld);
            Assert.False(during.RequiresRecovery);
            PrinterControlException error = await Assert.ThrowsAsync<PrinterControlException>(() =>
                service.AdmitAsync(printerId, Guid.NewGuid(), userId.ToString(), new(PrinterControlKind.HomeAll), default));
            Assert.Equal("physical_control_barrier", error.Code);
        });
        finish.SetResult(true);
        Assert.Equal(200, Assert.IsType<ObjectResult>((await stop.WaitAsync(TimeSpan.FromSeconds(3))).Result).StatusCode);
        Assert.False((await GetAsync(id)).BarrierHeld);
        actuation.Verify(service => service.QueueLifecycleAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        actuation.Verify(service => service.CompleteDirectAsync(It.IsAny<PrinterActuationLease>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(PrinterControlState.Unknown, (await GetAsync(id)).State);
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
    public async Task EmergencyStopAsync_AmbiguousHttpDelivery_ReleasesAfterMotionQuiescesWithoutInventingSuccess(bool running, string outcome)
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
            Assert.False(current.BarrierHeld);
            Assert.False(current.RequiresRecovery);
            Assert.Equal(running ? PrinterControlState.Unknown : PrinterControlState.Failed, current.State);
            Assert.Equal(running ? PrinterControlEvidence.None : PrinterControlEvidence.NotSent, current.CompletionEvidence);
            Assert.Equal(PrinterEmergencyStopDelivery.Unknown, (await db.PrinterEmergencyStopAttempts.SingleAsync()).Delivery);
            await service.SetOutcomeAsync(id, owner, true, null, default);
            Assert.Equal(current, await service.GetAsync(printerId, id, default));
        });
        printers.Verify(service => service.EmergencyStopAsync(printerId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        emergency.Verify(service => service.EmergencyStopAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmergencyStopAsync_CrashBeforeHttpWrite_FreshPendingBlocksThenExpiresWithoutReplaying(bool committed)
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
            Assert.True(current.BarrierHeld);
            Assert.False(current.RequiresRecovery);
            await service.ReconcileOrphansAsync(default);
            Assert.True((await service.GetAsync(printerId, id, default)).BarrierHeld);
            (await db.PrinterEmergencyStopAttempts.SingleAsync(a => a.Id == abandoned.AttemptId)).CreatedAtUtc =
                DateTime.UtcNow - PrinterControlOperationService.OwnerLiveness - TimeSpan.FromSeconds(1);
            await db.SaveChangesAsync();
            await service.ReconcileOrphansAsync(default);
            Assert.False((await service.GetAsync(printerId, id, default)).BarrierHeld);
        });
        Guid successor = Guid.NewGuid();
        await AdmitAsync(successor);
        await ChangeAsync(async (db, service) =>
        {
            Assert.False(await service.CommitEmergencyStopSendAsync(printerId, abandoned, userId.ToString(), default));
            await service.FinishEmergencyStopAsync(printerId, abandoned, PrinterEmergencyStopDelivery.Unknown, default);
            Assert.Equal(committed ? PrinterEmergencyStopDelivery.Unknown : PrinterEmergencyStopDelivery.NotSent,
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
            Assert.True((await service.GetAsync(printerId, id, default)).BarrierHeld);
        });
        firstResult.SetResult(false);
        Assert.Equal(503, Assert.IsType<ObjectResult>((await first.WaitAsync(TimeSpan.FromSeconds(3))).Result).StatusCode);
        await ChangeAsync(async (db, service) =>
        {
            await service.MaintainOwnerAsync(id, owner, true, default);
            Assert.Equal(1, await db.PrinterEmergencyStopAttempts.CountAsync(a => a.Delivery == PrinterEmergencyStopDelivery.Unknown));
            Assert.False((await service.GetAsync(printerId, id, default)).BarrierHeld);
            Assert.Equal(PrinterControlState.Unknown, (await service.GetAsync(printerId, id, default)).State);
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
    public async Task EmergencyStopAsync_LegacyFlags_DoNotRequireAttestationOrRetainIdleBarrier(bool flag)
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
            Assert.Equal(flag, (await db.PrinterControlOperations.SingleAsync()).EmergencyStopInFlight);
            PrinterControlOperationDto current = await service.GetAsync(printerId, id, default);
            Assert.False(current.BarrierHeld);
            Assert.False(current.RequiresRecovery);
            Assert.Equal(PrinterControlState.Failed, current.State);
            Assert.Equal(PrinterControlEvidence.NotSent, current.CompletionEvidence);
            Assert.Null((await db.PrinterControlOperations.SingleAsync()).RecoveryEvidenceJson);
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
            Assert.False((await service.GetAsync(printerId, id, default)).BarrierHeld);
            Assert.Equal(PrinterControlState.Failed, (await service.GetAsync(printerId, id, default)).State);
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
            Assert.False((await service.GetAsync(printerId, id, default)).BarrierHeld);
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
            Assert.True((await service.GetAsync(printerId, id, default)).BarrierHeld);
            Assert.False((await service.GetAsync(printerId, id, default)).RequiresRecovery);
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
        Assert.Equal(new PrinterControlRequest(kind, X: kind is PrinterControlKind.Jog or PrinterControlKind.MoveTo ? 1.5 : null,
            Y: kind == PrinterControlKind.MoveTo ? 2 : null, Z: kind == PrinterControlKind.MoveTo ? 10 : null), channel.Request);
        channel.Completion.SetResult();
        await WaitForStateAsync(id, PrinterControlState.Succeeded);
        await worker.StopAsync(default);
        Assert.Equal(1, channel.SendCount);
        safety.Verify(guard => guard.ValidateObservedManualMoveAsync(printerId,
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
        safety.Setup(guard => guard.ValidateObservedManualMoveAsync(printerId,
            It.IsAny<PrinterSafetyMoveRequest>(), It.IsAny<PrinterStatusDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterSafetyValidationResult.Reject(409, "printer_axes_not_homed", SensitiveFailureDetail));
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
        Assert.Equal(busy ? "printer_busy" : "printer_axes_not_homed", result.Failure?.Code);
        Assert.Contains(busy ? "ready and idle" : "home all axes", result.Failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveFailureDetail, result.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData("printer_telemetry_missing", "evidence is missing")]
    [InlineData("printer_telemetry_stale", "evidence expired")]
    [InlineData("printer_safety_evidence_unknown", "could not be verified")]
    [InlineData("printer_move_out_of_bounds", "configured travel bounds")]
    [InlineData("printer_configuration_changed", "configuration changed")]
    [InlineData("printer_operation_unsupported", "does not support")]
    [InlineData("unrecognized_diagnostic", "pre-send check could not complete")]
    public async Task SetOutcomeAsync_PreSendDenial_PreservesCodeAndActionableMessage(string code, string expected)
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (_, service) =>
        {
            Guid owner = Guid.NewGuid();
            Assert.NotNull(await service.ClaimAsync(id, owner, default));
            await service.SetOutcomeAsync(id, owner, false, code, default);
        });
        PrinterControlOperationDto result = await GetAsync(id);
        Assert.Equal(PrinterControlState.Failed, result.State);
        Assert.Equal(PrinterControlEvidence.NotSent, result.CompletionEvidence);
        Assert.Equal(code, result.Failure!.Code);
        Assert.Contains(expected, result.Failure.Message, StringComparison.Ordinal);
        Assert.False(result.BarrierHeld);
        Assert.Equal(0, channel.SendCount);
    }

    [Fact]
    public async Task WorkerAsync_FirmwareRejectsAfterSend_PreservesUnknownWithoutReplay()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PrinterControlOperationWorker>.Instance);
        await worker.StartAsync(default);
        await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        channel.Completion.SetException(new PrinterControlException(502, "printer_firmware_rejected", SensitiveFailureDetail));
        await WaitForStateAsync(id, PrinterControlState.Unknown);
        await worker.StopAsync(default);
        PrinterControlOperationDto result = await GetAsync(id);
        Assert.False(result.BarrierHeld);
        Assert.Equal(PrinterControlEvidence.None, result.CompletionEvidence);
        Assert.Equal("printer_firmware_rejected", result.Failure!.Code);
        Assert.Contains("Motion may have partially executed", result.Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("recovery", result.Failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SensitiveFailureDetail, result.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(result, await AdmitAsync(id));
        await worker.TickAsync(default);
        Assert.Equal(1, channel.SendCount);
    }

    [Fact]
    public async Task WorkerAsync_TransportFailure_SettlesUnknownAndReleasesWithoutReplayOrSensitiveLogs()
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
        Assert.False(unknown.BarrierHeld);
        Assert.False(unknown.RequiresRecovery);
        Assert.Equal("backend_outcome_unknown", unknown.Failure?.Code);
        WorkerLog warning = Assert.Single(logger.Entries);
        Assert.Equal(nameof(IOException), LogFields(warning)["ExceptionType"]);
        Assert.Equal(id, LogFields(warning)["OperationId"]);
        AssertSafeLogs(logger, SensitiveFailureDetail);
        await worker.StopAsync(default);
        Assert.True(channel.Disposed.Task.IsCompleted);
        await ChangeAsync(async (db, service) =>
        {
            (await db.PrinterControlOperations.SingleAsync()).OwnerHeartbeatAtUtc = DateTime.UtcNow.AddMinutes(-2);
            await db.SaveChangesAsync();
            await service.ReconcileOrphansAsync(default);
            Assert.Null(await service.ClaimAsync(id, Guid.NewGuid(), default));
            Assert.Null((await db.PrinterControlOperations.SingleAsync()).RecoveryEvidenceJson);
        });
        Assert.Equal(PrinterControlState.Unknown, (await GetAsync(id)).State);
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
            PrinterPhysicalControlDto projection = (await PrinterControlOperationService.ProjectAsync(db, [printerId], clients.Object, default))[printerId];
            Assert.Empty(projection.SupportedOperations);
            Assert.True(projection.BarrierHeld);
            Assert.False(projection.RequiresRecovery);
            Assert.Null(projection.OperationId);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletePrinterAsync_UnknownHistory_BlocksOnlyWhilePhysicalBarrierRetained(bool barrierHeld)
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (db, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            await service.CommitSendAsync(id, owner, default);
            if (barrierHeld)
            {
                (await db.PrinterControlOperations.SingleAsync()).State = PrinterControlState.Unknown;
                await db.SaveChangesAsync();
            }
            else
            {
                await service.SetOutcomeAsync(id, owner, false, "transport_lost", default);
            }
        });
        await ChangeAsync(async (db, _) =>
        {
            var repository = new EfPrintersRepository(db, Mock.Of<ISensitiveDataProtector>());
            if (barrierHeld)
            {
                PrinterControlException error = await Assert.ThrowsAsync<PrinterControlException>(() =>
                    repository.RemoveAsync(new Printer { Id = printerId }, default));
                Assert.Equal("physical_control_barrier", error.Code);
                Assert.True(await db.Printers.AnyAsync(p => p.Id == printerId));
                Assert.Equal(id, (await db.PrinterDispatchStates.AsNoTracking().SingleAsync()).PhysicalControlCommandId);
            }
            else
            {
                await repository.RemoveAsync(new Printer { Id = printerId }, default);
                Assert.False(await db.Printers.AnyAsync(p => p.Id == printerId));
            }
        });
        Assert.Equal(0, channel.SendCount);
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
        await ChangeAsync(async (db, service) =>
        {
            Guid successorOwner = Guid.NewGuid();
            Assert.NotNull(await service.ClaimAsync(id, successorOwner, default));
            Assert.False(await service.CommitSendAsync(id, owner, default));
            await service.SetOutcomeAsync(id, owner, false, "sender_interrupted", default);
            PrinterControlOperation operation = await db.PrinterControlOperations.AsNoTracking().SingleAsync();
            Assert.Equal(successorOwner, operation.OwnerToken);
            Assert.Equal(PrinterControlState.Queued, operation.State);
            Assert.Equal(id, (await db.PrinterDispatchStates.AsNoTracking().SingleAsync()).PhysicalControlCommandId);
        });
    }

    [Fact]
    public async Task ReconcileOrphansAsync_SentCrash_ReleasesWithoutEvidenceAndFencesLateCompletion()
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
        Assert.False(unknown.BarrierHeld);
        Assert.False(unknown.RequiresRecovery);
        Assert.Equal(PrinterControlEvidence.None, unknown.CompletionEvidence);
        await ChangeAsync(async (_, service) =>
        {
            Assert.Null(await service.ClaimAsync(id, Guid.NewGuid(), default));
            Assert.False(await service.CommitSendAsync(id, owner, default));
        });
        Guid successor = Guid.NewGuid();
        await AdmitAsync(successor);
        await ChangeAsync(async (db, service) =>
        {
            await service.SetOutcomeAsync(id, owner, true, null, default);
            Assert.Equal(successor, (await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
            PrinterControlOperation historical = await db.PrinterControlOperations.SingleAsync(o => o.Id == id);
            Assert.Null(historical.RecoveryEvidenceJson);
            Assert.Equal(PrinterControlState.Unknown, historical.State);
            Assert.Equal(PrinterControlEvidence.None, historical.CompletionEvidence);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetOutcomeAsync_PreSendVersusAmbiguousWrite_ReleasesBothWithTruthfulEvidence(bool sent)
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
        Assert.False(result.BarrierHeld);
        Assert.False(result.RequiresRecovery);
        Assert.Equal(sent ? PrinterControlState.Unknown : PrinterControlState.Failed, result.State);
        Assert.Equal(sent ? PrinterControlEvidence.None : PrinterControlEvidence.NotSent, result.CompletionEvidence);
    }

    [Theory]
    [InlineData(PrinterControlState.Succeeded, false)]
    [InlineData(PrinterControlState.Failed, false)]
    [InlineData(PrinterControlState.Unknown, false)]
    [InlineData(PrinterControlState.Succeeded, true)]
    [InlineData(PrinterControlState.Failed, true)]
    [InlineData(PrinterControlState.Unknown, true)]
    public async Task SetOutcomeAsync_ReleaseDeferred_TimestampsTerminalReceiptBeforeBarrierCleanup(
        PrinterControlState state, bool pendingEmergency)
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (db, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            if (state != PrinterControlState.Failed)
            {
                await service.CommitSendAsync(id, owner, default);
            }
            if (pendingEmergency)
            {
                db.PrinterEmergencyStopAttempts.Add(new PrinterEmergencyStopAttempt
                {
                    Id = Guid.NewGuid(), OperationId = id, PrinterId = printerId,
                    ActorSubject = userId.ToString(), ConfigurationIdentity = "test",
                    CreatedAtUtc = DateTime.UtcNow, Delivery = PrinterEmergencyStopDelivery.Pending,
                });
            }
            else
            {
                (await db.PrinterDispatchStates.SingleAsync()).ActiveJobId = Guid.NewGuid();
            }
            await db.SaveChangesAsync();
        });
        DateTime before = DateTime.UtcNow;
        await ChangeAsync(async (_, service) =>
            await service.SetOutcomeAsync(id, owner, state == PrinterControlState.Succeeded,
                state == PrinterControlState.Succeeded ? null : "sender_interrupted", default));
        PrinterControlOperationDto terminal = await GetAsync(id);
        Assert.Equal(state, terminal.State);
        Assert.True(terminal.BarrierHeld);
        Assert.False(terminal.RequiresRecovery);
        Assert.NotNull(terminal.CompletedAtUtc);
        Assert.InRange(terminal.CompletedAtUtc.Value, before, DateTime.UtcNow);
        Assert.Equal(state == PrinterControlState.Succeeded ? PrinterControlEvidence.MotionQueueDrained :
            state == PrinterControlState.Failed ? PrinterControlEvidence.NotSent : PrinterControlEvidence.None,
            terminal.CompletionEvidence);
        await ChangeAsync(async (db, service) =>
        {
            Assert.Equal(terminal.CompletedAtUtc, (await db.PrinterControlOperations.SingleAsync()).CompletedAtUtc);
            if (pendingEmergency)
            {
                (await db.PrinterEmergencyStopAttempts.SingleAsync()).Delivery = PrinterEmergencyStopDelivery.NotSent;
            }
            else
            {
                (await db.PrinterDispatchStates.SingleAsync()).ActiveJobId = null;
            }
            await db.SaveChangesAsync();
            await service.ReconcileOrphansAsync(default);
            await service.SetOutcomeAsync(id, owner, true, null, default);
        });
        PrinterControlOperationDto released = await GetAsync(id);
        Assert.False(released.BarrierHeld);
        Assert.Equal(state, released.State);
        Assert.Equal(terminal.CompletedAtUtc, released.CompletedAtUtc);
        Assert.Equal(terminal.CompletionEvidence, released.CompletionEvidence);
    }

    [Theory]
    [InlineData(PrinterEmergencyStopDelivery.Accepted, false)]
    [InlineData(PrinterEmergencyStopDelivery.NotSent, false)]
    [InlineData(PrinterEmergencyStopDelivery.Unknown, false)]
    [InlineData(PrinterEmergencyStopDelivery.Accepted, true)]
    [InlineData(PrinterEmergencyStopDelivery.NotSent, true)]
    [InlineData(PrinterEmergencyStopDelivery.Unknown, true)]
    public async Task FinishEmergencyStopAsync_SenderIsolated_PreservesEmergencyDiagnosticsAfterRelease(
        PrinterEmergencyStopDelivery delivery, bool motionSent)
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (_, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            if (motionSent)
            {
                await service.CommitSendAsync(id, owner, default);
            }
            PrinterEmergencyStopLease lease = (await service.PrepareEmergencyStopAsync(printerId, userId.ToString(), default))!;
            if (delivery != PrinterEmergencyStopDelivery.NotSent)
            {
                Assert.True(await service.CommitEmergencyStopSendAsync(printerId, lease, userId.ToString(), default));
            }
            await service.MaintainOwnerAsync(id, owner, true, default);
            Assert.True((await service.GetAsync(printerId, id, default)).BarrierHeld);
            await service.FinishEmergencyStopAsync(printerId, lease, delivery, default);
        });
        PrinterControlOperationDto receipt = await GetAsync(id);
        Assert.False(receipt.BarrierHeld);
        Assert.NotNull(receipt.CompletedAtUtc);
        Assert.Equal(motionSent ? PrinterControlState.Unknown : PrinterControlState.Failed, receipt.State);
        Assert.Equal(delivery switch
        {
            PrinterEmergencyStopDelivery.Accepted => "emergency_stop_accepted",
            PrinterEmergencyStopDelivery.NotSent => "emergency_stop_not_sent",
            _ => "emergency_stop_outcome_unknown",
        }, receipt.Failure?.Code);
        Assert.Equal("Motion was interrupted. Check the printer; no command will be replayed.", receipt.Failure?.Message);
        Assert.Equal(motionSent ? PrinterControlEvidence.None : PrinterControlEvidence.NotSent, receipt.CompletionEvidence);
    }

    [Fact]
    public async Task SetOutcomeAsync_LateMatchingResponse_DoesNotRewriteSettledUnknown()
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
        Assert.Equal(PrinterControlState.Unknown, result.State);
        Assert.Equal(PrinterControlEvidence.None, result.CompletionEvidence);
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
        Assert.False(result.BarrierHeld);
        Assert.False(result.RequiresRecovery);
    }

    [Theory]
    [InlineData("missing", "home", PrinterControlKind.HomeAll)]
    [InlineData("missing", "move", PrinterControlKind.Jog)]
    [InlineData("unresolvable", "home", PrinterControlKind.HomeAll)]
    [InlineData("unresolvable", "move", PrinterControlKind.Jog)]
    [InlineData("withdrawn", "home", PrinterControlKind.HomeAll)]
    [InlineData("withdrawn", "move", PrinterControlKind.Jog)]
    public async Task ImportLegacyAsync_UnavailableCapability_PreservesUnknownAuditAndReleasesWithoutReplay(
        string capabilityState, string legacyOperation, PrinterControlKind kind)
    {
        Guid id = Guid.NewGuid();
        await SeedUnavailableLegacyMotionAsync(id, legacyOperation, capabilityState);
        await ChangeAsync(async (db, service) =>
        {
            await service.ImportLegacyAsync(printerId, default);
            await service.ImportLegacyAsync(printerId, default);
            clients.Verify(f => f.GetClient(It.IsAny<int>()), Times.Never);
            PrinterControlOperation operation = await db.PrinterControlOperations.SingleAsync();
            Assert.Equal("legacy:unknown", operation.NormalizedIntent);
            Assert.NotNull(operation.SendCommittedAtUtc);
            Assert.Null(operation.OwnerToken);
            Assert.Null(operation.X);
            Assert.Null(operation.Y);
            Assert.Null(operation.Z);
            Assert.Equal(1, await db.QueueOperationAudits.CountAsync());
            Assert.Equal(1, await db.QueueDispatchOutbox.CountAsync());
            Assert.Null(await service.ClaimAsync(id, Guid.NewGuid(), default));
        });

        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PrinterControlOperationWorker>.Instance);
        await worker.TickAsync(default);
        PrinterControlOperationDto receipt = await GetAsync(id);
        Assert.Equal(kind, receipt.Kind);
        Assert.Equal(PrinterControlState.Unknown, receipt.State);
        Assert.Equal("legacy_outcome_unknown", receipt.Failure?.Code);
        Assert.False(receipt.BarrierHeld);
        Assert.False(receipt.RequiresRecovery);
        Assert.Equal(PrinterControlEvidence.None, receipt.CompletionEvidence);

        await ChangeAsync(async (db, service) =>
        {
            Assert.Null((await db.PrinterControlOperations.SingleAsync()).RecoveryEvidenceJson);
            Assert.Null((await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
            PrinterControlException unsupported = await Assert.ThrowsAsync<PrinterControlException>(() =>
                service.AdmitAsync(printerId, Guid.NewGuid(), userId.ToString(),
                    new(kind, X: kind == PrinterControlKind.Jog ? 1 : null), default));
            Assert.Equal("printer_operation_unsupported", unsupported.Code);
        });
        motionCapability.Verify(m => m.ConnectAsync(It.IsAny<Printer>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData("missing", "home")]
    [InlineData("missing", "move")]
    [InlineData("unresolvable", "home")]
    [InlineData("unresolvable", "move")]
    [InlineData("withdrawn", "home")]
    [InlineData("withdrawn", "move")]
    public async Task PrepareEmergencyStopAsync_UnavailableLegacyCapability_CleansHistoricalMotionWithoutCreatingSender(
        string capabilityState, string legacyOperation)
    {
        Guid id = Guid.NewGuid();
        await SeedUnavailableLegacyMotionAsync(id, legacyOperation, capabilityState);
        await ChangeAsync(async (db, service) =>
        {
            PrinterEmergencyStopLease? lease = await service.PrepareEmergencyStopAsync(printerId, userId.ToString(), default);
            Assert.Null(lease);
            clients.Verify(f => f.GetClient(It.IsAny<int>()), Times.Never);
            Assert.Empty(await db.PrinterEmergencyStopAttempts.ToArrayAsync());
            PrinterControlOperationDto receipt = await service.GetAsync(printerId, id, default);
            Assert.Equal(PrinterControlState.Unknown, receipt.State);
            Assert.False(receipt.RequiresRecovery);
            Assert.False(receipt.BarrierHeld);
            Assert.Equal(PrinterControlEvidence.None, receipt.CompletionEvidence);
        });
        motionCapability.Verify(m => m.ConnectAsync(It.IsAny<Printer>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData("extrude")]
    [InlineData("disable_motors")]
    [InlineData("async_motion")]
    public async Task ImportLegacyAsync_UnrecognizedBarrierLabel_DoesNotInventMotionReceipt(string operation)
    {
        Guid id = Guid.NewGuid();
        await SeedUnavailableLegacyMotionAsync(id, operation, "missing");
        await ChangeAsync(async (db, service) =>
        {
            await service.ImportLegacyAsync(printerId, default);
            Assert.Empty(await db.PrinterControlOperations.ToArrayAsync());
            Assert.Equal(id, (await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
        });
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
        Assert.Equal(new PrinterControlRequest(PrinterControlKind.HomeAll), channel.Request);
        Assert.Equal(PrinterControlState.Running, (await GetAsync(id)).State);
        channel.Completion.SetResult();
        await WaitForStateAsync(id, PrinterControlState.Succeeded);
        await worker.StopAsync(default);
        Assert.Equal(1, channel.SendCount);
    }

    [Fact]
    public async Task WorkerAsync_EmergencyCancellation_JoinsTransportBeforeReleasingBarrier()
    {
        Guid id = Guid.NewGuid();
        channel.HoldDisposal = true;
        await AdmitAsync(id);
        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PrinterControlOperationWorker>.Instance);
        await worker.StartAsync(default);
        await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        PrinterEmergencyStopLease lease = null!;
        await ChangeAsync(async (_, service) =>
            lease = (await service.PrepareEmergencyStopAsync(printerId, userId.ToString(), default))!);
        await channel.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(channel.Disposed.Task.IsCompleted);
            Assert.True((await GetAsync(id)).BarrierHeld);
            await ChangeAsync(async (_, service) =>
            {
                await service.FinishEmergencyStopAsync(printerId, lease, PrinterEmergencyStopDelivery.Unknown, default);
                await service.ReconcileOrphansAsync(default);
                Assert.True((await service.GetAsync(printerId, id, default)).BarrierHeld);
            });
        }
        finally
        {
            channel.AllowDisposal.TrySetResult();
        }
        await channel.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForStateAsync(id, PrinterControlState.Unknown);
        await worker.StopAsync(default);
        PrinterControlOperationDto isolated = await GetAsync(id);
        Assert.False(isolated.BarrierHeld);
        Assert.False(isolated.RequiresRecovery);
        Assert.Equal(PrinterControlEvidence.None, isolated.CompletionEvidence);
        Assert.Equal(1, channel.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControllerAsync_AdmissionAndSettledReplay_UseFixedWireContract(bool success)
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
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        }));
        Assert.Equal("Queued", json.RootElement.GetProperty("state").GetString());
        Assert.Equal("HomeAll", json.RootElement.GetProperty("kind").GetString());
        Assert.True(json.RootElement.GetProperty("barrierHeld").GetBoolean());
        Assert.False(json.RootElement.GetProperty("requiresRecovery").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("failure").ValueKind);

        Guid owner = Guid.NewGuid();
        await ChangeAsync(async (_, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            await service.CommitSendAsync(id, owner, default);
            await service.SetOutcomeAsync(id, owner, success, success ? null : "transport_lost", default);
        });
        var replay = Assert.IsType<OkObjectResult>(await controller.SubmitAsync(printerId, new(PrinterControlKind.HomeAll), id.ToString(), default));
        PrinterControlOperationDto receipt = Assert.IsType<PrinterControlOperationDto>(replay.Value);
        Assert.Equal(success ? PrinterControlState.Succeeded : PrinterControlState.Unknown, receipt.State);
        Assert.False(receipt.BarrierHeld);
        Assert.False(receipt.RequiresRecovery);
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("get")]
    [InlineData("current")]
    public async Task ControllerAsync_PrinterAccessDenied_DoesNotExposeOrMutateReceipt(string action)
    {
        Guid id = Guid.NewGuid();
        PrinterControlOperationDto admitted = await AdmitAsync(id);
        authorization.Setup(a => a.CanAccessPrinterAsync(It.IsAny<ClaimsPrincipal>(), printerId,
            It.IsAny<PrinterGroupAccessLevel>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        PrinterControlOperationsController controller = CreateMotionController(scope.ServiceProvider);
        IActionResult response = action switch
        {
            "submit" => await controller.SubmitAsync(printerId, new(PrinterControlKind.HomeAll), Guid.NewGuid().ToString(), default),
            "get" => await controller.GetAsync(printerId, id, default),
            _ => await controller.CurrentAsync(printerId, default),
        };
        Assert.Equal(404, Assert.IsType<ObjectResult>(response).StatusCode);
        Assert.Equal(admitted, await GetAsync(id));
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().PrinterControlOperations.CountAsync());
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetOutcomeAsync_LateOwnerWithReplacedBarrier_DoesNotReleaseOrMutateSuccessor(bool success)
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        Guid successor = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (db, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            await service.CommitSendAsync(id, owner, default);
            (await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId = successor;
            await db.SaveChangesAsync();
        });
        await ChangeAsync(async (db, service) =>
        {
            long revision = (await db.PrinterDispatchStates.SingleAsync()).Revision;
            int audits = await db.QueueOperationAudits.CountAsync();
            await service.SetOutcomeAsync(id, owner, success, success ? null : "sender_interrupted", default);
            await service.MaintainOwnerAsync(id, owner, true, default);
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            Assert.Equal(successor, barrier.PhysicalControlCommandId);
            Assert.Equal(revision, barrier.Revision);
            Assert.Equal(PrinterControlState.Running, (await db.PrinterControlOperations.SingleAsync()).State);
            Assert.Equal(audits, await db.QueueOperationAudits.CountAsync());
        });
    }

    [Fact]
    public async Task FinishEmergencyStopAsync_LatePendingAttempt_DoesNotReleaseSuccessor()
    {
        Guid id = Guid.NewGuid();
        Guid successor = Guid.NewGuid();
        await AdmitAsync(id);
        PrinterEmergencyStopLease lease = null!;
        await ChangeAsync(async (db, service) =>
        {
            lease = (await service.PrepareEmergencyStopAsync(printerId, userId.ToString(), default))!;
            Assert.True(await service.CommitEmergencyStopSendAsync(printerId, lease, userId.ToString(), default));
            (await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId = successor;
            await db.SaveChangesAsync();
        });
        await ChangeAsync(async (db, service) =>
        {
            long revision = (await db.PrinterDispatchStates.SingleAsync()).Revision;
            await service.FinishEmergencyStopAsync(printerId, lease, PrinterEmergencyStopDelivery.Unknown, default);
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            Assert.Equal(successor, barrier.PhysicalControlCommandId);
            Assert.Equal(revision, barrier.Revision);
            Assert.Equal(PrinterEmergencyStopDelivery.Unknown, (await db.PrinterEmergencyStopAttempts.SingleAsync()).Delivery);
        });
    }

    [Theory]
    [InlineData(PrinterControlState.Running)]
    [InlineData(PrinterControlState.Unknown)]
    [InlineData(PrinterControlState.Recovering)]
    [InlineData(PrinterControlState.Succeeded)]
    [InlineData(PrinterControlState.Failed)]
    [InlineData(PrinterControlState.Recovered)]
    public async Task ReconcileOrphansAsync_HistoricalRetainedBarrier_ReleasesOnceWithoutRewritingEvidence(PrinterControlState state)
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        PrinterControlEvidence evidence = state == PrinterControlState.Succeeded
            ? PrinterControlEvidence.MotionQueueDrained
            : state == PrinterControlState.Recovered ? PrinterControlEvidence.OperatorVerifiedRecovery : PrinterControlEvidence.None;
        DateTime original = DateTime.UtcNow.AddHours(-1);
        await ChangeAsync(async (db, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            await service.CommitSendAsync(id, owner, default);
            PrinterControlOperation operation = await db.PrinterControlOperations.SingleAsync();
            operation.State = state;
            operation.OwnerHeartbeatAtUtc = original;
            operation.CompletionEvidence = evidence;
            operation.EmergencyStopInFlight = true;
            operation.RecoveryRequestedAtUtc = original;
            operation.RecoveryActorSubject = "historical-operator";
            operation.RecoveryFromRevision = 7;
            operation.RecoveryEvidenceJson = "{\"historical\":true}";
            (await db.PrinterDispatchStates.SingleAsync()).PhysicalControlRequiresReconciliation = true;
            await db.SaveChangesAsync();
        });

        // A new scope simulates restart: cleanup cannot depend on live worker memory or plugin availability.
        clients.Invocations.Clear();
        clients.Setup(f => f.GetClient(It.IsAny<int>())).Throws(new InvalidOperationException("Missing plugin"));
        await ChangeAsync(async (db, service) =>
        {
            await service.ReconcileOrphansAsync(default);
            PrinterControlOperation operation = await db.PrinterControlOperations.SingleAsync();
            Assert.Equal(state is PrinterControlState.Running or PrinterControlState.Recovering ? PrinterControlState.Unknown : state, operation.State);
            Assert.Equal(evidence, operation.CompletionEvidence);
            Assert.True(operation.Settled);
            Assert.False(operation.RequiresRecovery);
            Assert.NotNull(operation.CompletedAtUtc);
            Assert.Equal(original, operation.RecoveryRequestedAtUtc);
            Assert.Equal("historical-operator", operation.RecoveryActorSubject);
            Assert.Equal(7, operation.RecoveryFromRevision);
            Assert.Equal("{\"historical\":true}", operation.RecoveryEvidenceJson);
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            Assert.Null(barrier.PhysicalControlCommandId);
            Assert.False(barrier.PhysicalControlRequiresReconciliation);
            int audits = await db.QueueOperationAudits.CountAsync();
            int events = await db.QueueDispatchOutbox.CountAsync();
            await service.ReconcileOrphansAsync(default);
            Assert.Equal(audits, await db.QueueOperationAudits.CountAsync());
            Assert.Equal(events, await db.QueueDispatchOutbox.CountAsync());
            Assert.Null(await service.ClaimAsync(id, Guid.NewGuid(), default));
        });
        clients.Verify(f => f.GetClient(It.IsAny<int>()), Times.Never);
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData("physical-attempt", false)]
    [InlineData("dispatch-attempt", false)]
    [InlineData("active-job", false)]
    [InlineData("Starting", false)]
    [InlineData("Printing", false)]
    [InlineData("Paused", false)]
    [InlineData("physical-attempt", true)]
    [InlineData("dispatch-attempt", true)]
    [InlineData("active-job", true)]
    [InlineData("Starting", true)]
    [InlineData("Printing", true)]
    [InlineData("Paused", true)]
    public async Task ManualCleanupAsync_PrintOccupancy_PreservesEveryIndependentFence(string fence, bool legacy)
    {
        Guid id = Guid.NewGuid();
        Guid protectedId = Guid.NewGuid();
        if (!legacy)
        {
            await AdmitAsync(id);
        }

        await ChangeAsync(async (db, _) =>
        {
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            barrier.PhysicalControlCommandId = id;
            barrier.PhysicalControlOperation = legacy ? "home" : "async_motion";
            barrier.PhysicalControlRequiresReconciliation = true;
            if (fence == "physical-attempt")
            {
                barrier.PhysicalControlAttemptId = protectedId;
            }
            else if (fence == "dispatch-attempt")
            {
                barrier.ActiveDispatchAttemptId = protectedId;
            }
            else if (fence == "active-job")
            {
                barrier.ActiveJobId = protectedId;
            }
            else
            {
                db.PrintJobs.Add(new PrintJob
                {
                    Id = protectedId, Name = "Occupying job", AssignedPrinterId = printerId,
                    Status = Enum.Parse<PrintJobStatus>(fence),
                });
            }
            if (!legacy)
            {
                PrinterControlOperation operation = await db.PrinterControlOperations.SingleAsync();
                operation.State = PrinterControlState.Unknown;
                operation.OwnerHeartbeatAtUtc = DateTime.UtcNow.AddHours(-1);
            }
            await db.SaveChangesAsync();
        });

        await ChangeAsync(async (db, service) =>
        {
            int audits = await db.QueueOperationAudits.CountAsync();
            await service.ImportLegacyAsync(printerId, default);
            await service.ReconcileOrphansAsync(default);
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            Assert.Equal(id, barrier.PhysicalControlCommandId);
            Assert.True(barrier.PhysicalControlRequiresReconciliation);
            Assert.Equal(fence == "physical-attempt" ? protectedId : (Guid?)null, barrier.PhysicalControlAttemptId);
            Assert.Equal(fence == "dispatch-attempt" ? protectedId : (Guid?)null, barrier.ActiveDispatchAttemptId);
            Assert.Equal(fence == "active-job" ? protectedId : (Guid?)null, barrier.ActiveJobId);
            Assert.Equal(legacy ? 0 : 1, await db.PrinterControlOperations.CountAsync());
            Assert.Equal(audits, await db.QueueOperationAudits.CountAsync());
            if (Enum.TryParse(fence, out PrintJobStatus status))
            {
                PrintJob job = await db.PrintJobs.SingleAsync();
                Assert.Equal(protectedId, job.Id);
                Assert.Equal(printerId, job.AssignedPrinterId);
                Assert.Equal(status, job.Status);
            }
        });
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData(PrinterControlState.Running)]
    [InlineData(PrinterControlState.Unknown)]
    [InlineData(PrinterControlState.Recovering)]
    public async Task ReconcileOrphansAsync_FreshOwnerWithoutIsolation_RetainsCoordination(PrinterControlState state)
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (db, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            await service.CommitSendAsync(id, owner, default);
            (await db.PrinterControlOperations.SingleAsync()).State = state;
            await db.SaveChangesAsync();
        });
        await ChangeAsync(async (_, service) =>
        {
            await service.ReconcileOrphansAsync(default);
            PrinterControlOperationDto receipt = await service.GetAsync(printerId, id, default);
            Assert.Equal(state, receipt.State);
            Assert.True(receipt.BarrierHeld);
            Assert.False(receipt.RequiresRecovery);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MaintainOwnerAsync_HeartbeatGap_OnlyRenewsUnexpiredSenderLease(bool sent, bool expired)
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        DateTime heartbeat = DateTime.UtcNow.AddSeconds(expired ? -21 : -5);
        await AdmitAsync(id);
        await ChangeAsync(async (db, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            if (sent)
            {
                await service.CommitSendAsync(id, owner, default);
            }
            (await db.PrinterControlOperations.SingleAsync()).OwnerHeartbeatAtUtc = heartbeat;
            await db.SaveChangesAsync();
        });
        await ChangeAsync(async (db, service) =>
        {
            PrinterControlOperationDto before = await service.GetAsync(printerId, id, default);
            Assert.Equal(!expired, await service.MaintainOwnerAsync(id, owner, false, default));
            PrinterControlOperation operation = await db.PrinterControlOperations.AsNoTracking().SingleAsync();
            if (expired)
            {
                Assert.Equal(heartbeat, operation.OwnerHeartbeatAtUtc);
            }
            else
            {
                Assert.True(operation.OwnerHeartbeatAtUtc > heartbeat);
            }
            Assert.Equal(before, await service.GetAsync(printerId, id, default));
            await service.ReconcileOrphansAsync(default);
            Assert.True((await service.GetAsync(printerId, id, default)).BarrierHeld);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task CommitSendAsync_ExpiredSenderLeaseOrWrongBarrier_NeverCommitsPhysicalSend(bool replacedBarrier, bool nullHeartbeat)
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        Guid successor = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (db, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            if (replacedBarrier)
            {
                (await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId = successor;
            }
            else
            {
                (await db.PrinterControlOperations.SingleAsync()).OwnerHeartbeatAtUtc =
                    nullHeartbeat ? null : DateTime.UtcNow - PrinterControlOperationService.SenderLease - TimeSpan.FromSeconds(1);
            }
            await db.SaveChangesAsync();
        });
        await ChangeAsync(async (db, service) =>
        {
            if (replacedBarrier)
            {
                PrinterControlException error = await Assert.ThrowsAsync<PrinterControlException>(() => service.CommitSendAsync(id, owner, default));
                Assert.Equal("physical_control_barrier", error.Code);
            }
            else
            {
                Assert.False(await service.CommitSendAsync(id, owner, default));
            }
            Assert.Null((await db.PrinterControlOperations.SingleAsync()).SendCommittedAtUtc);
            Assert.Equal(replacedBarrier ? successor : id, (await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
        });
        Assert.Equal(0, channel.SendCount);
    }

    [Fact]
    public async Task CommitEmergencyStopSendAsync_ExpiredUncommittedAttempt_CannotSendWhileFreshPendingStillBlocksSuccessor()
    {
        Guid id = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (db, service) =>
        {
            PrinterEmergencyStopLease lease = (await service.PrepareEmergencyStopAsync(printerId, userId.ToString(), default))!;
            (await db.PrinterEmergencyStopAttempts.SingleAsync()).CreatedAtUtc = DateTime.UtcNow.AddSeconds(-25);
            await db.SaveChangesAsync();
            Assert.False(await service.CommitEmergencyStopSendAsync(printerId, lease, userId.ToString(), default));
            await service.ReconcileOrphansAsync(default);
            Assert.True((await service.GetAsync(printerId, id, default)).BarrierHeld);
            Assert.Null((await db.PrinterEmergencyStopAttempts.SingleAsync()).SendCommittedAtUtc);
            Assert.Equal(PrinterEmergencyStopDelivery.Pending, (await db.PrinterEmergencyStopAttempts.SingleAsync()).Delivery);
        });
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerAsync_OutcomeArrives_DisposesAndJoinsBeforeWritingOutcomeOrReleasing(bool success)
    {
        Guid id = Guid.NewGuid();
        channel.HoldDisposal = true;
        await AdmitAsync(id);
        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PrinterControlOperationWorker>.Instance);
        await worker.StartAsync(default);
        await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (success)
        {
            channel.Completion.TrySetResult();
        }
        else
        {
            channel.Completion.TrySetException(new IOException("Response lost"));
        }
        await channel.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            PrinterControlOperationDto during = await GetAsync(id);
            Assert.Equal(PrinterControlState.Running, during.State);
            Assert.True(during.BarrierHeld);
            Assert.False(channel.Disposed.Task.IsCompleted);
        }
        finally
        {
            channel.AllowDisposal.TrySetResult();
        }
        await WaitForStateAsync(id, success ? PrinterControlState.Succeeded : PrinterControlState.Unknown);
        await worker.StopAsync(default);
        Assert.True(channel.Disposed.Task.IsCompleted);
        Assert.False((await GetAsync(id)).BarrierHeld);
        Assert.Equal(1, channel.SendCount);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("home_xy")]
    [InlineData("home_z")]
    [InlineData("move")]
    [InlineData("move_to")]
    public async Task ImportLegacyAsync_FreshDirectSender_OnlyCleansAfterDeadlineAndOrphanGrace(string operation)
    {
        Guid id = Guid.NewGuid();
        await ChangeAsync(async (db, service) =>
        {
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            barrier.PhysicalControlCommandId = id;
            barrier.PhysicalControlOperation = operation;
            barrier.PhysicalControlStartedAtUtc = DateTime.UtcNow.AddMinutes(-5);
            barrier.PhysicalControlRequiresReconciliation = false;
            await db.SaveChangesAsync();
            await service.ImportLegacyAsync(printerId, default);
            Assert.Equal(id, barrier.PhysicalControlCommandId);
            Assert.Empty(await db.PrinterControlOperations.ToArrayAsync());
            Assert.Empty(await db.QueueOperationAudits.ToArrayAsync());
            barrier.PhysicalControlStartedAtUtc =
                DateTime.UtcNow - PrinterControlOperationService.CommandTimeout - PrinterControlOperationService.OwnerLiveness - TimeSpan.FromSeconds(1);
            await db.SaveChangesAsync();
        });
        await ChangeAsync(async (db, service) =>
        {
            await service.ImportLegacyAsync(printerId, default);
            Assert.Null((await db.PrinterDispatchStates.SingleAsync()).PhysicalControlCommandId);
            PrinterControlOperation receipt = await db.PrinterControlOperations.SingleAsync();
            Assert.Equal(id, receipt.Id);
            Assert.Equal(PrinterControlState.Unknown, receipt.State);
            Assert.Equal(PrinterControlEvidence.None, receipt.CompletionEvidence);
            Assert.False(receipt.RequiresRecovery);
            Assert.Null(await service.ClaimAsync(id, Guid.NewGuid(), default));
        });
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData("home", false, false)]
    [InlineData("home_xy", false, false)]
    [InlineData("home_z", false, false)]
    [InlineData("move", false, false)]
    [InlineData("move_to", false, false)]
    [InlineData("home", true, true)]
    [InlineData("move", true, true)]
    [InlineData("disable_motors", false, true)]
    [InlineData("emergencystop", false, true)]
    [InlineData("start", true, true)]
    public async Task MarkDirectUnknownAsync_ManualOnlyLease_ReleasesOnlyIdleMotionAndKeepsUnknownAudit(
        string operation, bool attemptBound, bool retained)
    {
        Guid id = Guid.NewGuid();
        Guid? attempt = attemptBound ? Guid.NewGuid() : null;
        await ChangeAsync(async (db, _) =>
        {
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            barrier.PhysicalControlCommandId = id;
            barrier.PhysicalControlAttemptId = attempt;
            barrier.PhysicalControlOperation = operation;
            barrier.PhysicalControlStartedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            var service = new PrinterPhysicalActuationService(db, new DbOutboxSequenceAllocator(),
                authorization.Object, NullLogger<PrinterPhysicalActuationService>.Instance);
            await service.MarkDirectUnknownAsync(new(id, printerId, attempt, operation, userId.ToString()), "transport_lost", default);
            Assert.Equal(retained ? id : (Guid?)null, barrier.PhysicalControlCommandId);
            Assert.Equal(attempt, barrier.PhysicalControlAttemptId);
            Assert.Equal(retained, barrier.PhysicalControlRequiresReconciliation);
            QueueDispatchOutbox notification = await db.QueueDispatchOutbox.SingleAsync();
            Assert.Equal(PrinterPhysicalActuationService.EventTypeUnknown, notification.EventType);
            Assert.Equal(QueueAuditOutcomes.Unknown, (await db.QueueOperationAudits.SingleAsync()).Outcome);
            Assert.Empty(await db.PrinterControlOperations.ToArrayAsync());
        });
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData("dispatch-attempt")]
    [InlineData("active-job")]
    [InlineData("Starting")]
    [InlineData("Printing")]
    [InlineData("Paused")]
    public async Task MarkDirectUnknownAsync_PrintOccupancyAppears_PreservesManualBarrierAndPrintState(string fence)
    {
        Guid id = Guid.NewGuid();
        Guid protectedId = Guid.NewGuid();
        await ChangeAsync(async (db, _) =>
        {
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            barrier.PhysicalControlCommandId = id;
            barrier.PhysicalControlOperation = "move";
            if (fence == "dispatch-attempt")
            {
                barrier.ActiveDispatchAttemptId = protectedId;
            }
            else if (fence == "active-job")
            {
                barrier.ActiveJobId = protectedId;
            }
            else
            {
                db.PrintJobs.Add(new PrintJob
                {
                    Id = protectedId, Name = "Occupying", AssignedPrinterId = printerId,
                    Status = Enum.Parse<PrintJobStatus>(fence),
                });
            }
            await db.SaveChangesAsync();
            var service = new PrinterPhysicalActuationService(db, new DbOutboxSequenceAllocator(),
                authorization.Object, NullLogger<PrinterPhysicalActuationService>.Instance);
            await service.MarkDirectUnknownAsync(new(id, printerId, null, "move", userId.ToString()), "transport_lost", default);
            Assert.Equal(id, barrier.PhysicalControlCommandId);
            Assert.True(barrier.PhysicalControlRequiresReconciliation);
            Assert.Equal(fence == "dispatch-attempt" ? protectedId : (Guid?)null, barrier.ActiveDispatchAttemptId);
            Assert.Equal(fence == "active-job" ? protectedId : (Guid?)null, barrier.ActiveJobId);
            if (Enum.TryParse(fence, out PrintJobStatus status))
            {
                Assert.Equal(status, (await db.PrintJobs.SingleAsync()).Status);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerAsync_SenderLeaseOrCommandDeadlineExpires_CancelsAndJoinsBeforeSettlingUnknown(bool renewLease)
    {
        Assert.Equal(TimeSpan.FromSeconds(20), PrinterControlOperationService.SenderLease);
        Assert.Equal(TimeSpan.FromMinutes(5), PrinterControlOperationService.CommandTimeout);
        Assert.True(PrinterControlOperationService.SenderLease < PrinterControlOperationService.OwnerLiveness);
        Guid id = Guid.NewGuid();
        channel.HoldDisposal = true;
        var clock = new MotionTestClock();
        await AdmitAsync(id);
        using var worker = new PrinterControlOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PrinterControlOperationWorker>.Instance, clock);
        await worker.TickAsync(default);
        await channel.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (renewLease)
        {
            for (int renewal = 0; renewal < 19; renewal++)
            {
                clock.Advance(TimeSpan.FromSeconds(15));
                await worker.TickAsync(default);
                Assert.False(channel.Disposing.Task.IsCompleted);
            }
            clock.Advance(TimeSpan.FromSeconds(14));
            await worker.TickAsync(default);
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(19));
        }
        Assert.False(channel.Disposing.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await channel.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(PrinterControlState.Running, (await GetAsync(id)).State);
            Assert.True((await GetAsync(id)).BarrierHeld);
            Assert.False(channel.Disposed.Task.IsCompleted);
        }
        finally
        {
            channel.AllowDisposal.TrySetResult();
        }
        await WaitForStateAsync(id, PrinterControlState.Unknown);
        await worker.TickAsync(default);
        PrinterControlOperationDto receipt = await GetAsync(id);
        Assert.Equal("sender_interrupted", receipt.Failure?.Code);
        Assert.Equal(PrinterControlEvidence.None, receipt.CompletionEvidence);
        Assert.False(receipt.BarrierHeld);
        Assert.False(receipt.RequiresRecovery);
        Assert.Equal(1, channel.SendCount);
    }

    [Fact]
    public async Task EmergencyStopAsync_AttemptBoundOwnershipAppearsBeforeSend_PreservesUnrelatedAttempt()
    {
        Guid id = Guid.NewGuid();
        Guid attempt = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (db, service) =>
        {
            PrinterEmergencyStopLease lease = (await service.PrepareEmergencyStopAsync(printerId, userId.ToString(), default))!;
            (await db.PrinterDispatchStates.SingleAsync()).PhysicalControlAttemptId = attempt;
            await db.SaveChangesAsync();
            Assert.False(await service.CommitEmergencyStopSendAsync(printerId, lease, userId.ToString(), default));
            await service.FinishEmergencyStopAsync(printerId, lease, PrinterEmergencyStopDelivery.NotSent, default);
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            Assert.Equal(id, barrier.PhysicalControlCommandId);
            Assert.Equal(attempt, barrier.PhysicalControlAttemptId);
            Assert.Null((await db.PrinterEmergencyStopAttempts.SingleAsync()).SendCommittedAtUtc);
        });
        Assert.Equal(0, channel.SendCount);
    }

    [Theory]
    [InlineData("physical-attempt")]
    [InlineData("dispatch-attempt")]
    [InlineData("active-job")]
    [InlineData("Starting")]
    [InlineData("Printing")]
    [InlineData("Paused")]
    public async Task PrepareEmergencyStopAsync_UnrelatedPrintOwnership_DoesNotAdmitOrMutate(string fence)
    {
        Guid id = Guid.NewGuid();
        Guid protectedId = Guid.NewGuid();
        await AdmitAsync(id);
        await ChangeAsync(async (db, _) =>
        {
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            if (fence == "physical-attempt")
            {
                barrier.PhysicalControlAttemptId = protectedId;
            }
            else if (fence == "dispatch-attempt")
            {
                barrier.ActiveDispatchAttemptId = protectedId;
            }
            else if (fence == "active-job")
            {
                barrier.ActiveJobId = protectedId;
            }
            else
            {
                db.PrintJobs.Add(new PrintJob
                {
                    Id = protectedId, Name = "Occupying", AssignedPrinterId = printerId,
                    Status = Enum.Parse<PrintJobStatus>(fence),
                });
            }
            await db.SaveChangesAsync();
        });
        await ChangeAsync(async (db, service) =>
        {
            PrinterControlOperationDto before = await service.GetAsync(printerId, id, default);
            PrinterControlException denied = await Assert.ThrowsAsync<PrinterControlException>(() =>
                service.PrepareEmergencyStopAsync(printerId, userId.ToString(), default));
            Assert.Equal(fence == "physical-attempt" ? "physical_control_barrier" : "printer_busy", denied.Code);
            Assert.Equal(before, await service.GetAsync(printerId, id, default));
            Assert.Empty(await db.PrinterEmergencyStopAttempts.ToArrayAsync());
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            Assert.Equal(fence == "physical-attempt" ? protectedId : (Guid?)null, barrier.PhysicalControlAttemptId);
            Assert.Equal(fence == "dispatch-attempt" ? protectedId : (Guid?)null, barrier.ActiveDispatchAttemptId);
            Assert.Equal(fence == "active-job" ? protectedId : (Guid?)null, barrier.ActiveJobId);
        });
        Assert.Equal(0, channel.SendCount);
    }

    [Fact]
    public async Task EmergencyStopAsync_DeadlineExpiresDuringSendAuthorization_NeverInvokesBackend()
    {
        Guid id = Guid.NewGuid();
        var clock = new MotionTestClock();
        await AdmitAsync(id);
        int checks = 0;
        authentication.Setup(service => service.HasPermissionAsync(userId, "queue", "cancel"))
            .Returns(() =>
            {
                if (++checks == 2)
                {
                    clock.Advance(TimeSpan.FromSeconds(20));
                }
                return Task.FromResult(true);
            });
        var printers = new Mock<IPrintersService>(MockBehavior.Strict);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateEmergencyController(scope.ServiceProvider, printers, clock).EmergencyStopAsync(printerId, default));
        Assert.Equal(2, checks);
        printers.Verify(service => service.EmergencyStopAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        await ChangeAsync(async (db, service) =>
        {
            PrinterEmergencyStopAttempt attempt = await db.PrinterEmergencyStopAttempts.SingleAsync();
            Assert.Null(attempt.SendCommittedAtUtc);
            Assert.Equal(PrinterEmergencyStopDelivery.NotSent, attempt.Delivery);
            Assert.False((await service.GetAsync(printerId, id, default)).BarrierHeld);
        });
    }

    [Fact]
    public async Task EmergencyStopAsync_SenderDeadlineExpires_CancelsBackendAndNeverRetriesOrFabricatesAcceptance()
    {
        Guid id = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        var clock = new MotionTestClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken backendToken = default;
        await AdmitAsync(id);
        await ChangeAsync(async (_, service) =>
        {
            await service.ClaimAsync(id, owner, default);
            await service.CommitSendAsync(id, owner, default);
        });
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.EmergencyStopAsync(printerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, string _, CancellationToken token) =>
            {
                backendToken = token;
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        Task<ActionResult<CommandResult>> request =
            CreateEmergencyController(scope.ServiceProvider, printers, clock).EmergencyStopAsync(printerId, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.False(backendToken.IsCancellationRequested);
        Assert.False(request.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        ObjectResult response = Assert.IsType<ObjectResult>((await request.WaitAsync(TimeSpan.FromSeconds(10))).Result);
        Assert.Equal(503, response.StatusCode);
        Assert.True(backendToken.IsCancellationRequested);
        await ChangeAsync(async (db, service) =>
        {
            Assert.Equal(PrinterEmergencyStopDelivery.Unknown, (await db.PrinterEmergencyStopAttempts.SingleAsync()).Delivery);
            Assert.True((await service.GetAsync(printerId, id, default)).BarrierHeld);
            await service.MaintainOwnerAsync(id, owner, true, default);
            PrinterControlOperationDto receipt = await service.GetAsync(printerId, id, default);
            Assert.Equal(PrinterControlState.Unknown, receipt.State);
            Assert.Equal(PrinterControlEvidence.None, receipt.CompletionEvidence);
            Assert.False(receipt.BarrierHeld);
        });
        printers.Verify(service => service.EmergencyStopAsync(printerId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
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

    private PrintersController CreateEmergencyController(IServiceProvider services, Mock<IPrintersService> printers,
        TimeProvider? timeProvider = null)
    {
        var real = new PrinterPhysicalActuationService(services.GetRequiredService<AppDbContext>(),
            services.GetRequiredService<IDbOutboxSequenceAllocator>(), authorization.Object, NullLogger<PrinterPhysicalActuationService>.Instance);
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        PrintersController controller = PrintersControllerControlGuardsTests.CreateController(printers,
            new Mock<IPrinterStatusCacheReader>(), out _, actuation: actuation,
            motionControl: services.GetRequiredService<PrinterControlOperationService>(), timeProvider: timeProvider);
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

    private async Task SeedUnavailableLegacyMotionAsync(Guid id, string operation, string capabilityState)
    {
        const int backend = 9001;
        if (capabilityState == "withdrawn")
        {
            clients.Setup(f => f.GetClient(backend)).Returns(client.Object);
            motionCapability.SetupGet(m => m.SupportedMotionKinds).Returns([PrinterControlKind.HomeZ]);
        }
        else if (capabilityState == "unresolvable")
        {
            clients.Setup(f => f.GetClient(backend)).Throws(new InvalidOperationException("Plugin cannot be resolved."));
        }
        else
        {
            clients.Setup(f => f.GetClient(backend)).Throws(new ArgumentException("Plugin is not installed."));
        }

        await ChangeAsync(async (db, _) =>
        {
            (await db.Printers.SingleAsync()).Backend = backend;
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync();
            barrier.PhysicalControlCommandId = id;
            barrier.PhysicalControlOperation = operation;
            barrier.PhysicalControlActorSubject = userId.ToString();
            barrier.PhysicalControlStartedAtUtc = DateTime.UtcNow.AddMinutes(-5);
            barrier.PhysicalControlRequiresReconciliation = true;
            await db.SaveChangesAsync();
        });
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

    private sealed class FakeChannel : IPrinterMotionChannel
    {
        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldDisposal { get; set; }
        public int SendCount { get; private set; }
        public bool Idle { get; set; } = true;
        public PrinterControlRequest? Request { get; private set; }
        public Guid? CorrelationId { get; private set; }
        public Task<bool> IsIdleAsync(CancellationToken ct) => Task.FromResult(Idle);
        public PrinterStatusDto? MotionState { get; set; }
        public Task<PrinterStatusDto> ReadMotionStateAsync(Guid printerId, CancellationToken ct) =>
            Task.FromResult(MotionState ?? new PrinterStatusDto(printerId, true, "Idle", X: 50, Y: 50, Z: 10,
                SafetyTelemetry: PrinterSafetyTelemetryDto.Empty with
                {
                    HomedAxes = new(["x", "y", "z"], DateTime.UtcNow, 15, "test"),
                    CoordinateOriginOffsetMm = new(new(0, 0, 0), DateTime.UtcNow, 15, "test"),
                }));
        public async Task ExecuteAsync(Guid correlationId, PrinterControlRequest request, CancellationToken ct)
        {
            SendCount++;
            Request = request;
            CorrelationId = correlationId;
            Sent.SetResult();
            await Completion.Task.WaitAsync(ct);
        }
        public async ValueTask DisposeAsync()
        {
            Disposing.TrySetResult();
            if (HoldDisposal)
            {
                await AllowDisposal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Disposed.TrySetResult();
        }
    }

    internal sealed class MotionTestClock : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<MotionTimer> timers = [];
        private TimeSpan elapsed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (sync)
            {
                var timer = new MotionTimer(this, callback, state);
                timer.Change(dueTime, period);
                timers.Add(timer);
                return timer;
            }
        }

        public void Advance(TimeSpan duration)
        {
            lock (sync)
            {
                elapsed += duration;
                foreach (MotionTimer timer in timers.ToArray())
                {
                    timer.FireIfDue();
                }
            }
        }

        private sealed class MotionTimer(MotionTestClock clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan? due;
            private bool disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (clock.sync)
                {
                    if (disposed)
                    {
                        return false;
                    }
                    due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.elapsed + dueTime;
                    return true;
                }
            }

            public void FireIfDue()
            {
                if (!disposed && due is TimeSpan at && at <= clock.elapsed)
                {
                    due = null;
                    callback(state);
                }
            }

            public void Dispose()
            {
                lock (clock.sync)
                {
                    disposed = true;
                    due = null;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
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
