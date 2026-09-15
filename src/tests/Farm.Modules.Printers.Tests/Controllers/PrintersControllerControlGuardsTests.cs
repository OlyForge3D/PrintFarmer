using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Telemetry;
using Farm.Modules.Printers.Controllers;
using Farm.Modules.Printers.Controllers.Requests;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Modules.Printers.Tests.Controllers;

/// <summary>
/// Unit tests for the server-side capability guards on PrintersController control endpoints
/// (/temps, /move, /moveto, /home, /homexy, /homez). See GitHub issues
/// OlyForge3D/PrintFarmer#290 and OlyForge3D/PrintFarmer#314.
/// </summary>
public class PrintersControllerControlGuardsTests
{
    [Fact]
    public async Task ReadAfterProjectionDeletion_Returns404WithoutUnlockedFallback()
    {
        Guid id = Guid.NewGuid();
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.GetStatusDtoAsync(id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Collections.Generic.KeyNotFoundException("Printer not found."));
        printers.Setup(service => service.GetPrinterDtoAsync(id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Collections.Generic.KeyNotFoundException("Printer not found."));
        PrintersController controller = CreateController(printers, new Mock<IPrinterStatusCacheReader>(), out _);
        Assert.IsType<NotFoundResult>((await controller.GetStatusAsync(id, default)).Result);
        Assert.IsType<NotFoundResult>((await controller.GetAsync(id, default)).Result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetStatusAsync_RequiresViewAndPreservesObservedSafetyFacts(bool allowed)
    {
        Guid id = Guid.NewGuid();
        var printers = new Mock<IPrintersService>();
        var authorization = new Mock<IQueueResourceAuthorizationService>();
        PrintersController controller = CreateController(printers, new Mock<IPrinterStatusCacheReader>(), out _, resourceAuthorization: authorization);
        authorization.Setup(service => service.CanAccessPrinterAsync(
            It.IsAny<ClaimsPrincipal>(), id, PrinterGroupAccessLevel.View, It.IsAny<CancellationToken>()))
            .ReturnsAsync(allowed);
        var status = new PrinterStatusDto(id, true, "idle", SafetyTelemetry: PrinterSafetyTelemetryDto.Empty with
        {
            HomedAxes = new SafetyAxesTelemetryFactDto(["x", "y", "z"], DateTime.UtcNow, 15, "moonraker:toolhead.homed_axes"),
        });
        printers.Setup(service => service.GetStatusDtoAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(status);

        ActionResult<PrinterStatusDto> result = await controller.GetStatusAsync(id, default);

        if (allowed)
        {
            Assert.Same(status, Assert.IsType<OkObjectResult>(result.Result).Value);
        }
        else
        {
            Assert.IsType<NotFoundResult>(result.Result);
        }

        printers.Verify(service => service.GetStatusDtoAsync(id, It.IsAny<CancellationToken>()), allowed ? Times.Once() : Times.Never());
        var cache = Assert.IsType<ResponseCacheAttribute>(Attribute.GetCustomAttribute(
            typeof(PrintersController).GetMethod(nameof(PrintersController.GetStatusAsync))!, typeof(ResponseCacheAttribute)));
        Assert.True(cache.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cache.Location);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("homexy")]
    [InlineData("homez")]
    [InlineData("move")]
    [InlineData("moveto")]
    public async Task DirectMotionAsync_Moonraker_SendsOnceWithoutTrackedCapability(string route)
    {
        Guid id = Guid.NewGuid();
        var printers = new Mock<IPrintersService>();
        printers.Setup(p => p.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer { Id = id, Backend = (int)PrinterBackend.Moonraker });
        printers.Setup(p => p.GetMovementStatusAsync(It.Is<Printer>(printer => printer.Id == id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(id, true, "Idle", X: 10, Y: 20, Z: 0));
        printers.Setup(p => p.SendHomeAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        printers.Setup(p => p.HomeXYAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        printers.Setup(p => p.HomeZAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        printers.Setup(p => p.MoveAsync(id, 1, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterControlOutcome.Ok);
        printers.Setup(p => p.MoveToAsync(id, 1, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterControlOutcome.Ok);
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        PrintersController controller = CreateController(printers, new Mock<IPrinterStatusCacheReader>(), out _, actuation: actuation);
        ActionResult<CommandResult> response = route switch
        {
            "home" => await controller.HomeAsync(id, default),
            "homexy" => await controller.HomeXYAsync(id, default),
            "homez" => await controller.HomeZAsync(id, default),
            "move" => await controller.MoveAsync(id, new MoveRequest(1, null, null, null), default),
            _ => await controller.MoveToAsync(id, new MoveRequest(1, null, null, null), default),
        };
        Assert.True(Assert.IsType<CommandResult>(response.Value).Success);
        actuation.Verify(a => a.AcquireDirectAsync(id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        printers.Verify(p => p.GetMovementStatusAsync(
            It.Is<Printer>(printer => printer.Id == id), It.IsAny<CancellationToken>()), Times.Once);
        printers.Verify(p => p.GetStatusDtoAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(printers.Invocations, invocation => invocation.Method.Name is "SendHomeAsync" or "HomeXYAsync" or "HomeZAsync" or "MoveAsync" or "MoveToAsync");
        Assert.All(printers.Invocations, invocation => Assert.Contains(invocation.Method.Name,
            new[] { "FindByIdAsync", "GetMovementStatusAsync", "SendHomeAsync", "HomeXYAsync", "HomeZAsync", "MoveAsync", "MoveToAsync" }));
    }

    [Theory]
    [InlineData(PrinterBackend.Moonraker)]
    [InlineData(PrinterBackend.OctoPrint)]
    [InlineData(PrinterBackend.SDCP)]
    [InlineData(PrinterBackend.PrusaLink)]
    [InlineData(PrinterBackend.FlashForge)]
    public async Task DirectMotionAsync_SupportedBackend_DoesNotRequireTrackedTransport(PrinterBackend backend)
    {
        Guid id = Guid.NewGuid();
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer { Id = id, Backend = (int)backend });
        printers.Setup(p => p.GetMovementStatusAsync(It.Is<Printer>(printer => printer.Id == id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(id, true, "Idle", X: 10, Y: 20, Z: 0));
        printers.Setup(value => value.MoveAsync(id, 1, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterControlOutcome.Ok);
        var client = new Mock<IBackendClient>();
        var factory = new Mock<IBackendClientFactory>();
        factory.Setup(value => value.GetClient(backend)).Returns(client.Object);
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        PrintersController controller = CreateController(
            printers, new Mock<IPrinterStatusCacheReader>(), out _, actuation: actuation, backendClients: factory.Object);

        ActionResult<CommandResult> response = await controller.MoveAsync(id, new MoveRequest(1, null, null, null), default);

        Assert.True(Assert.IsType<CommandResult>(response.Value).Success);
        actuation.Verify(value => value.AcquireDirectAsync(id, It.IsAny<string>(), "move", It.IsAny<CancellationToken>()), Times.Once);
        printers.Verify(value => value.MoveAsync(id, 1, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(PrinterControlOutcome.BackendUnsupported, 502)]
    [InlineData(PrinterControlOutcome.BackendUnreachable, 503)]
    public async Task DirectMotionAsync_BackendRejects_DoesNotReportSuccessOrRetry(PrinterControlOutcome outcome, int statusCode)
    {
        Guid id = Guid.NewGuid();
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer { Id = id, Backend = (int)PrinterBackend.Moonraker });
        printers.Setup(p => p.GetMovementStatusAsync(It.Is<Printer>(printer => printer.Id == id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(id, true, "Idle", X: 10, Y: 20, Z: 0));
        printers.Setup(value => value.MoveAsync(id, 1, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        var actuation = new Mock<IPrinterPhysicalActuationService>();
        PrintersController controller = CreateController(
            printers, new Mock<IPrinterStatusCacheReader>(), out _, actuation: actuation);

        ActionResult<CommandResult> response = await controller.MoveAsync(id, new MoveRequest(1, null, null, null), default);

        ObjectResult result = Assert.IsAssignableFrom<ObjectResult>(response.Result);
        Assert.Equal(statusCode, result.StatusCode);
        Assert.False(Assert.IsType<CommandResult>(result.Value).Success);
        printers.Verify(value => value.MoveAsync(id, 1, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    internal static PrintersController CreateController(
        Mock<IPrintersService> printersService,
        Mock<IPrinterStatusCacheReader> statusCache,
        out Mock<IPrintFarmerTelemetryService> telemetry,
        Mock<IPrinterSafetyGuard>? safetyGuard = null,
        Mock<IPrinterPhysicalActuationService>? actuation = null,
        Mock<IQueueResourceAuthorizationService>? resourceAuthorization = null,
        Mock<IPrinterBackendCapabilitiesService>? capabilitiesService = null,
        IBackendClientFactory? backendClients = null,
        TimeProvider? timeProvider = null,
        IPrinterPhysicalActuationService? realActuation = null)
    {
        telemetry = new Mock<IPrintFarmerTelemetryService>();

        actuation ??= new Mock<IPrinterPhysicalActuationService>();
        actuation.Setup(service => service.AcquireDirectAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                Guid printerId,
                string actor,
                string operation,
                CancellationToken ct) =>
            {
                Printer? printer = await printersService.Object.FindByIdAsync(printerId, ct);
                if (printer is null)
                {
                    return new PrinterActuationResult(
                        PrinterActuationResultCode.PrinterNotFound);
                }

                PrinterStatusDto? status = statusCache.Object.GetStatus(printerId);
                if (PrinterControlGate.IsBusyForControl(status?.State))
                {
                    return new PrinterActuationResult(
                        PrinterActuationResultCode.PrinterBusy,
                        Detail: $"Printer is currently {status?.State?.ToLowerInvariant()}.");
                }

                return new PrinterActuationResult(
                    PrinterActuationResultCode.Accepted,
                    new PrinterActuationLease(
                        Guid.NewGuid(),
                        printerId,
                        null,
                        operation,
                        actor));
            });
        actuation.Setup(service => service.AcquireActiveAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                Guid printerId,
                string actor,
                string operation,
                CancellationToken _) =>
                new PrinterActuationResult(
                    PrinterActuationResultCode.Accepted,
                    new PrinterActuationLease(
                        Guid.NewGuid(),
                        printerId,
                        Guid.NewGuid(),
                        operation,
                        actor)));
        actuation.Setup(service => service.CompleteDirectAsync(
                It.IsAny<PrinterActuationLease>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        actuation.Setup(service => service.RevalidateDirectAsync(
                It.IsAny<PrinterActuationLease>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterActuationLease lease, CancellationToken _) =>
                new PrinterActuationResult(
                    PrinterActuationResultCode.Accepted,
                    lease,
                    lease.CommandId));
        actuation.Setup(service => service.MarkDirectUnknownAsync(
                It.IsAny<PrinterActuationLease>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        resourceAuthorization ??=
            new Mock<IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(service => service.CanAccessPrinterAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<Guid>(),
                It.IsAny<Farm.Infrastructure.Domain.PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        if (backendClients is null)
        {
            var factory = new Mock<IBackendClientFactory>();
            factory.Setup(value => value.GetClient(It.IsAny<PrinterBackend>())).Returns(Mock.Of<IBackendClient>());
            backendClients = factory.Object;
        }

        var controller = new PrintersController(
            logger: Mock.Of<ILogger<PrintersController>>(),
            printersService: printersService.Object,
            catalogService: Mock.Of<Farm.Modules.Printers.Services.Catalog.ICatalogService>(),
            validator: Mock.Of<IValidator<CreatePrinterFromDiscoveryDto>>(),
            discoveryProxyService: Mock.Of<Farm.Infrastructure.Services.Discovery.IDiscoveryProxyService>(),
            discoverySessions: Mock.Of<Farm.Infrastructure.Services.Discovery.IDiscoverySessionRegistry>(),
            printerBackendCapabilitiesService:
                capabilitiesService?.Object ??
                Mock.Of<IPrinterBackendCapabilitiesService>(),
            backendClientFactory: backendClients,
            httpClientFactory: Mock.Of<IHttpClientFactory>(),
            egressGuard: Farm.Testing.Shared.AppDbTestHelpers.PermissiveEgressGuard(),
            obicoServerAssignment: Mock.Of<Farm.Infrastructure.Services.FailureDetection.IObicoServerAssignmentService>(),
            settingsService: Mock.Of<Farm.Infrastructure.Settings.ISettingsService>(),
            printerSessionTimelineService: Mock.Of<IPrinterSessionTimelineService>(),
            telemetryService: telemetry.Object,
            bedTypeService: Mock.Of<Farm.Infrastructure.Services.BedTypes.IBedTypeService>(),
            physicalActuationService: realActuation ?? actuation.Object,
            queueResourceAuthorization: resourceAuthorization.Object,
            printerSafetyGuard:
                safetyGuard?.Object ?? CreatePermissiveSafetyGuard(),
            timeProvider: timeProvider);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                ], "test")),
            },
        };
        return controller;
    }

    [Theory]
    [InlineData("change", PrinterSafetyOperation.MmuChangeTool)]
    [InlineData("load", PrinterSafetyOperation.MmuLoad)]
    [InlineData("eject", PrinterSafetyOperation.MmuEject)]
    [InlineData("qidibox-unload", PrinterSafetyOperation.FilamentUnload)]
    [InlineData("qidibox-eject", PrinterSafetyOperation.FilamentUnload)]
    [InlineData("afc-load", PrinterSafetyOperation.FilamentChange)]
    public async Task MmuPhysicalFilamentRoute_RevalidatesSafetyBeforeBackendIo(
        string route,
        PrinterSafetyOperation expectedOperation)
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(service => service.FindByIdAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(cache => cache.GetStatus(id))
            .Returns(new PrinterStatusDto(id, true, "Idle"));
        var guard = new Mock<IPrinterSafetyGuard>();
        guard.Setup(service => service.ValidateAsync(
                id,
                expectedOperation,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterSafetyValidationResult.Reject(
                503,
                "printer_safety_evidence_unknown",
                "Unknown."));
        var capabilities = new Mock<IPrinterBackendCapabilitiesService>();
        PrintersController controller = CreateController(
            printersService,
            statusCache,
            out _,
            guard,
            capabilitiesService: capabilities);

        ActionResult<CommandResult> result = route switch
        {
            "change" => await controller.MmuChangeToolAsync(
                id,
                1,
                CancellationToken.None),
            "load" => await controller.MmuLoadAsync(
                id,
                CancellationToken.None),
            "eject" => await controller.MmuEjectAsync(
                id,
                CancellationToken.None),
            "qidibox-unload" => await controller.MmuGateActionAsync(
                id,
                new MmuGateActionRequest
                {
                    Protocol = "Qidibox",
                    Action = "Unload",
                    GateIndex = 1,
                },
                CancellationToken.None),
            "qidibox-eject" => await controller.MmuGateActionAsync(
                id,
                new MmuGateActionRequest
                {
                    Protocol = "Qidibox",
                    Action = "Eject",
                    GateIndex = 1,
                },
                CancellationToken.None),
            _ => await controller.MmuGateActionAsync(
                id,
                new MmuGateActionRequest
                {
                    Protocol = "Afc",
                    Action = "Load",
                    LaneName = "lane_1",
                },
                CancellationToken.None),
        };

        ObjectResult problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, problem.StatusCode);
        guard.Verify(service => service.ValidateAsync(
            id,
            expectedOperation,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
        capabilities.Verify(
            service => service.InvalidateVerifiedSafety(id),
            Times.Never());
        printersService.Verify(service => service.SendGcodeAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        printersService.Verify(service => service.ExecuteMmuAsync(
            It.IsAny<Guid>(),
            It.IsAny<MmuControlRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBackendCapabilitiesAsync_FiltersAccessBeforeDiscovery()
    {
        Guid allowedId = Guid.NewGuid();
        Guid deniedId = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(service => service.GetAllAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                SamplePrinter(allowedId),
                SamplePrinter(deniedId),
            ]);
        var authorization = new Mock<IQueueResourceAuthorizationService>();
        authorization.Setup(service => service.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<IReadOnlyCollection<Guid>>(),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { allowedId });
        var capabilities = new Mock<IPrinterBackendCapabilitiesService>();
        capabilities.Setup(service => service.GetByIdsAsync(
                It.Is<Guid[]>(ids =>
                    ids.Length == 1 &&
                    ids[0] == allowedId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PrinterBackendCapabilitiesDto(
                    allowedId,
                    "allowed",
                    PrinterBackend.Moonraker),
            ]);
        PrintersController controller = CreateController(
            printersService,
            new Mock<IPrinterStatusCacheReader>(),
            out _,
            resourceAuthorization: authorization,
            capabilitiesService: capabilities);

        ActionResult<IEnumerable<PrinterBackendCapabilitiesDto>> result =
            await controller.GetBackendCapabilitiesAsync(
                CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PrinterBackendCapabilitiesDto[] body =
            Assert.IsAssignableFrom<IEnumerable<PrinterBackendCapabilitiesDto>>(
                ok.Value).ToArray();
        Assert.Single(body);
        Assert.Equal(allowedId, body[0].PrinterId);
        capabilities.Verify(service => service.GetAllAsync(
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoadFilamentAsync_UnsupportedSafetyEvidence_Returns422WithoutBackendIo()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(service => service.FindByIdAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(cache => cache.GetStatus(id))
            .Returns(new PrinterStatusDto(id, true, "Idle"));
        var guard = new Mock<IPrinterSafetyGuard>();
        guard.Setup(service => service.ValidateAsync(
                id,
                PrinterSafetyOperation.FilamentLoad,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterSafetyValidationResult.Reject(
                422,
                "printer_operation_unsupported",
                "Unsupported."));
        PrintersController controller = CreateController(
            printersService,
            statusCache,
            out _,
            guard);

        ActionResult<CommandResult> result =
            await controller.LoadFilamentAsync(id, CancellationToken.None);

        ObjectResult problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(422, problem.StatusCode);
        ProblemDetails details = Assert.IsType<ProblemDetails>(problem.Value);
        Assert.Equal(
            "https://printfarmer.dev/problems/printer_operation_unsupported",
            details.Type);
        printersService.Verify(
            service => service.LoadFilamentAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MoveToAsync_LeaseRevalidationConflict_Returns409WithoutBackendIo()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(service => service.FindByIdAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(cache => cache.GetStatus(id))
            .Returns(new PrinterStatusDto(id, true, "Idle"));
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        actuation.Setup(service => service.AcquireDirectAsync(
                id,
                It.IsAny<string>(),
                "move_to",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid printerId, string actor, string operation, CancellationToken _) =>
                new PrinterActuationResult(
                    PrinterActuationResultCode.Accepted,
                    new PrinterActuationLease(
                        Guid.NewGuid(),
                        printerId,
                        null,
                        operation,
                        actor)));
        actuation.Setup(service => service.RevalidateDirectAsync(
                It.IsAny<PrinterActuationLease>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterActuationResult(
                PrinterActuationResultCode.ConcurrencyConflict,
                Detail: "Ownership changed."));
        actuation.Setup(service => service.CompleteDirectAsync(
                It.IsAny<PrinterActuationLease>(),
                false,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        PrintersController controller = CreateController(
            printersService,
            statusCache,
            out _,
            actuation: actuation);
        actuation.Setup(service => service.RevalidateDirectAsync(
                It.IsAny<PrinterActuationLease>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterActuationResult(
                PrinterActuationResultCode.ConcurrencyConflict,
                Detail: "Ownership changed."));

        ActionResult<CommandResult> result = await controller.MoveToAsync(
            id,
            new MoveRequest(10, 10, 10, null),
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        printersService.Verify(
            service => service.MoveToAsync(
                It.IsAny<Guid>(),
                It.IsAny<double?>(),
                It.IsAny<double?>(),
                It.IsAny<double?>(),
                It.IsAny<double?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LoadFilamentAsync_SafetyGuardThrows_ReleasesLeaseBeforeRethrow()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(service => service.FindByIdAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(cache => cache.GetStatus(id))
            .Returns(new PrinterStatusDto(id, true, "Idle"));
        var guard = new Mock<IPrinterSafetyGuard>();
        guard.Setup(service => service.ValidateAsync(
                id,
                PrinterSafetyOperation.FilamentLoad,
                null,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("probe failed"));
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        PrintersController controller = CreateController(
            printersService,
            statusCache,
            out _,
            guard,
            actuation);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.LoadFilamentAsync(id, CancellationToken.None));

        actuation.Verify(service => service.CompleteDirectAsync(
            It.IsAny<PrinterActuationLease>(),
            false,
            "printer_safety_revalidation_failed",
            CancellationToken.None), Times.Once);
        printersService.Verify(service => service.LoadFilamentAsync(
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static IPrinterSafetyGuard CreatePermissiveSafetyGuard()
    {
        var guard = new Mock<IPrinterSafetyGuard>();
        guard.Setup(service => service.ValidateObservedManualMoveAsync(
                It.IsAny<Guid>(), It.IsAny<PrinterSafetyMoveRequest>(),
                It.IsAny<PrinterStatusDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterSafetyValidationResult.Allowed);
        guard.Setup(service => service.ValidateAsync(
                It.IsAny<Guid>(),
                It.IsAny<PrinterSafetyOperation>(),
                It.IsAny<PrinterSafetyMoveRequest?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterSafetyValidationResult.Allowed);
        return guard.Object;
    }

    private static Printer SamplePrinter(Guid id) => new()
    {
        Id = id,
        Name = "printer-1",
        ServerUrl = "http://printer-1.local",
    };

    [Theory]
    [InlineData(0, 300)]
    [InlineData(101, 300)]
    [InlineData(-101, 300)]
    [InlineData(double.NaN, 300)]
    [InlineData(double.PositiveInfinity, 300)]
    [InlineData(double.NegativeInfinity, 300)]
    [InlineData(5, 0)]
    [InlineData(5, 6001)]
    public async Task ExtrudeFilamentAsync_InvalidBounds_ProduceZeroBackendIo(
        double distance,
        int feedrate)
    {
        var printersService = new Mock<IPrintersService>(MockBehavior.Strict);
        var statusCache = new Mock<IPrinterStatusCacheReader>(MockBehavior.Strict);
        PrintersController controller = CreateController(
            printersService,
            statusCache,
            out _);

        ActionResult<CommandResult> result = await controller.ExtrudeFilamentAsync(
            Guid.NewGuid(),
            new ExtrudeFilamentRequest
            {
                DistanceMm = distance,
                FeedrateMmPerMinute = feedrate,
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        printersService.VerifyNoOtherCalls();
        statusCache.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExtrudeFilamentAsync_ValidRequest_DelegatesSemanticValues()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(service => service.FindByIdAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        printersService.Setup(service => service.ExtrudeFilamentAsync(
                id,
                -5,
                300,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(cache => cache.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Idle"));
        PrintersController controller = CreateController(
            printersService,
            statusCache,
            out _);

        ActionResult<CommandResult> result = await controller.ExtrudeFilamentAsync(
            id,
            new ExtrudeFilamentRequest
            {
                DistanceMm = -5,
                FeedrateMmPerMinute = 300,
            },
            CancellationToken.None);

        Assert.True(result.Value?.Success);
        printersService.Verify(service => service.ExtrudeFilamentAsync(
            id,
            -5,
            300,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveZOffsetAsync_FirmwareResult_PersistsOnlyAfterSemanticSuccess(bool accepted)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        using ServiceProvider services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        Guid id = Guid.NewGuid();
        Printer printer = SamplePrinter(id);
        printer.ZOffsetMm = 0.1m;
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(printer);
        printers.Setup(service => service.SaveZOffsetToFirmwareAsync(id, -0.25m, It.IsAny<CancellationToken>()))
            .Callback(() => Assert.Equal(0.1m, printer.ZOffsetMm))
            .ReturnsAsync(accepted);
        printers.Setup(service => service.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        PrintersController controller = CreateController(
            printers, new Mock<IPrinterStatusCacheReader>(), out _, actuation: actuation);
        controller.HttpContext.RequestServices = services;
        controller.Request.Headers.IfMatch = $"\"{Convert.ToBase64String(printer.RowVersion!)}\"";

        ActionResult<CommandResult> result = await controller.SaveZOffsetAsync(
            id, new ZOffsetSaveRequest { OffsetMm = -0.25m, SaveToFirmware = true }, CancellationToken.None);

        if (accepted)
        {
            Assert.True(result.Value?.Success);
            Assert.Equal(-0.25m, printer.ZOffsetMm);
            printers.Verify(service => service.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            Assert.Equal(503, Assert.IsType<ObjectResult>(result.Result).StatusCode);
            Assert.Equal(0.1m, printer.ZOffsetMm);
            printers.Verify(service => service.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
            actuation.Verify(service => service.MarkDirectUnknownAsync(
                It.IsAny<PrinterActuationLease>(), "z_offset_firmware_outcome_unknown", It.IsAny<CancellationToken>()), Times.Once);
        }

        printers.Verify(service => service.SaveZOffsetToFirmwareAsync(id, -0.25m, It.IsAny<CancellationToken>()), Times.Once);
        printers.Verify(service => service.SendGcodeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("extrude")]
    [InlineData("disable-motors")]
    [InlineData("moveto")]
    public async Task EssentialControlAsync_PrintingPrinter_RejectsWithoutBackendIo(string operation)
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(service => service.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(cache => cache.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Printing"));
        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = operation switch
        {
            "extrude" => await controller.ExtrudeFilamentAsync(id,
                new ExtrudeFilamentRequest { DistanceMm = -5, FeedrateMmPerMinute = 300 },
                CancellationToken.None),
            "disable-motors" => await controller.DisableMotorsAsync(id, CancellationToken.None),
            _ => await controller.MoveToAsync(id, new MoveRequest(X: 0, Y: null, Z: 10, F: null), CancellationToken.None),
        };

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.False(Assert.IsType<CommandResult>(conflict.Value).Success);
        printersService.Verify(service => service.FindByIdAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        printersService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EmergencyStopAsync_IdlePrinter_ExecutesDirectBackendControl()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(service => service.FindByIdAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        printersService.Setup(service => service.EmergencyStopAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(cache => cache.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Idle"));
        PrintersController controller = CreateController(
            printersService,
            statusCache,
            out _);

        ActionResult<CommandResult> result = await controller.EmergencyStopAsync(
            id,
            CancellationToken.None);

        Assert.True(result.Value?.Success);
        printersService.Verify(service => service.EmergencyStopAsync(
            id,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("Qidibox", "Load", 2, null)]
    [InlineData("Qidibox", "Unload", 2, null)]
    [InlineData("Qidibox", "Eject", 2, null)]
    [InlineData("Afc", "Load", null, "lane_2")]
    [InlineData("Afc", "Unload", null, "lane-2")]
    public async Task MmuGateActionAsync_ValidTypedAction_DelegatesSemanticRequest(
        string protocol,
        string action,
        int? gateIndex,
        string? laneName)
    {
        Guid id = Guid.NewGuid();
        var expectedRequest = new MmuControlRequest(
            Enum.Parse<MmuControlAction>(action),
            Enum.Parse<MmuControlProtocol>(protocol),
            GateIndex: gateIndex,
            LaneName: laneName);
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(service => service.FindByIdAsync(
                id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        printersService.Setup(service => service.ExecuteMmuAsync(
                id,
                expectedRequest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(cache => cache.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Idle"));
        PrintersController controller = CreateController(
            printersService,
            statusCache,
            out _);

        ActionResult<CommandResult> result = await controller.MmuGateActionAsync(
            id,
            new MmuGateActionRequest
            {
                Protocol = protocol,
                Action = action,
                GateIndex = gateIndex,
                LaneName = laneName,
            },
            CancellationToken.None);

        Assert.True(result.Value?.Success);
        printersService.Verify(service => service.ExecuteMmuAsync(
            id,
            expectedRequest,
            It.IsAny<CancellationToken>()), Times.Once);
        printersService.Verify(service => service.SendGcodeAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Qidibox", "Load", 99, null)]
    [InlineData("Qidibox", "Start", 1, null)]
    [InlineData("Afc", "Load", null, "lane 1; START_PRINT")]
    [InlineData("Afc", "Eject", null, "lane1")]
    public async Task MmuGateActionAsync_InvalidTypedAction_ProducesZeroBackendIo(
        string protocol,
        string action,
        int? gateIndex,
        string? laneName)
    {
        var printersService = new Mock<IPrintersService>(MockBehavior.Strict);
        var statusCache = new Mock<IPrinterStatusCacheReader>(MockBehavior.Strict);
        PrintersController controller = CreateController(
            printersService,
            statusCache,
            out _);

        ActionResult<CommandResult> result = await controller.MmuGateActionAsync(
            Guid.NewGuid(),
            new MmuGateActionRequest
            {
                Protocol = protocol,
                Action = action,
                GateIndex = gateIndex,
                LaneName = laneName,
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        printersService.VerifyNoOtherCalls();
        statusCache.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetTempsAsync_ReturnsConflict_WhenPrinterIsPrinting()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));

        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(c => c.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Printing"));

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.SetTempsAsync(
            id, new TempTargets(Hotend: 210, Bed: 60), CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        CommandResult body = Assert.IsType<CommandResult>(conflict.Value);
        Assert.False(body.Success);
        Assert.Equal("Printer is currently printing.", body.Message);
        printersService.Verify(s => s.SetTempsAsync(It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveAsync_ReturnsConflict_WhenPrinterIsPrinting()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));

        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(c => c.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Printing"));

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.MoveAsync(
            id, new MoveRequest(X: 10, Y: null, Z: null, F: 3000), CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        CommandResult body = Assert.IsType<CommandResult>(conflict.Value);
        Assert.False(body.Success);
        Assert.Equal("Printer is currently printing.", body.Message);
        printersService.Verify(s => s.MoveAsync(It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveAsync_ReturnsNotFound_WhenPrinterMissing()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Printer?)null);

        var statusCache = new Mock<IPrinterStatusCacheReader>();

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.MoveAsync(
            id, new MoveRequest(X: 10, Y: null, Z: null, F: 3000), CancellationToken.None);

        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        CommandResult body = Assert.IsType<CommandResult>(notFound.Value);
        Assert.False(body.Success);
        Assert.Equal("Printer not found.", body.Message);
        printersService.Verify(s => s.MoveAsync(It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetPrintJobObjectsAsync_ReturnsOk_WithObjects()
    {
        Guid id = Guid.NewGuid();
        var objects = new PrintJobObjectListDto(
            id,
            "plate.gcode",
            new[] { new PrintJobObjectDto("cube", IsExcluded: false, IsCurrent: true) });
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.GetPrintJobObjectsAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(objects);
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<PrintJobObjectListDto> result = await controller.GetPrintJobObjectsAsync(id, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PrintJobObjectListDto body = Assert.IsType<PrintJobObjectListDto>(ok.Value);
        Assert.Equal(id, body.PrinterId);
        Assert.Equal("cube", body.Objects.Single().Name);
    }

    [Fact]
    public async Task GetPrintJobObjectsAsync_ReturnsNotFound_WhenPrinterMissing()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.GetPrintJobObjectsAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJobObjectListDto?)null);
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<PrintJobObjectListDto> result = await controller.GetPrintJobObjectsAsync(id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task ExcludePrintJobObjectAsync_ReturnsBadRequest_WhenObjectNameEmpty()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.ExcludePrintJobObjectAsync(
            id,
            new ExcludePrintJobObjectRequest(" "),
            CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        CommandResult body = Assert.IsType<CommandResult>(badRequest.Value);
        Assert.False(body.Success);
        printersService.Verify(s => s.ExcludePrintJobObjectAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExcludePrintJobObjectAsync_ReturnsOk_WhenServiceSucceeds()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.ExcludePrintJobObjectAsync(id, "cube", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(true, "Object skipped"));
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        PrintersController controller = CreateController(printersService, statusCache, out Mock<IPrintFarmerTelemetryService> telemetry);

        ActionResult<CommandResult> result = await controller.ExcludePrintJobObjectAsync(
            id,
            new ExcludePrintJobObjectRequest("cube"),
            CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        CommandResult body = Assert.IsType<CommandResult>(ok.Value);
        Assert.True(body.Success);
        telemetry.Verify(t => t.RecordPrinterOperation("exclude_object", id.ToString(), true), Times.Once);
    }

    [Fact]
    public async Task ExcludePrintJobObjectAsync_ReturnsBadRequest_WhenServiceRejectsRequest()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.ExcludePrintJobObjectAsync(id, "cube", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(false, "No active printing job is available for object exclusion."));
        var statusCache = new Mock<IPrinterStatusCacheReader>();
        PrintersController controller = CreateController(printersService, statusCache, out Mock<IPrintFarmerTelemetryService> telemetry);

        ActionResult<CommandResult> result = await controller.ExcludePrintJobObjectAsync(
            id,
            new ExcludePrintJobObjectRequest("cube"),
            CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        CommandResult body = Assert.IsType<CommandResult>(badRequest.Value);
        Assert.False(body.Success);
        Assert.Equal("No active printing job is available for object exclusion.", body.Message);
        telemetry.Verify(t => t.RecordPrinterOperation("exclude_object", id.ToString(), false), Times.Once);
    }

    [Fact]
    public async Task SetTempsAsync_ReturnsOk_WhenPrinterIdle()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        printersService.Setup(s => s.SetTempsAsync(id, 210, 60, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterControlOutcome.Ok);

        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(c => c.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Idle"));

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.SetTempsAsync(
            id, new TempTargets(Hotend: 210, Bed: 60), CancellationToken.None);

        CommandResult body = Assert.IsType<CommandResult>(result.Value);
        Assert.True(body.Success);
        Assert.Null(body.Message);
        printersService.Verify(s => s.SetTempsAsync(id, 210, 60, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Full-path test: service layer returns <see cref="PrinterControlOutcome.BackendBusy"/>
    /// (mapped from a thrown <see cref="PrinterBackendBusyException"/> inside the service).
    /// The controller must surface this as HTTP 409 Conflict, not 502 (#318 blocker 1).
    /// </summary>
    [Fact]
    public async Task SetTempsAsync_Returns409Conflict_WhenServiceReturnsBackendBusy()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        printersService.Setup(s => s.SetTempsAsync(id, 210, 60, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterControlOutcome.BackendBusy);

        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(c => c.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Idle"));

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.SetTempsAsync(
            id, new TempTargets(Hotend: 210, Bed: 60), CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        CommandResult body = Assert.IsType<CommandResult>(conflict.Value);
        Assert.False(body.Success);
        Assert.NotNull(body.Message);
    }

    [Fact]
    public async Task MoveAsync_Returns409Conflict_WhenServiceReturnsBackendBusy()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        printersService.Setup(s => s.MoveAsync(id, It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrinterControlOutcome.BackendBusy);

        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(c => c.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Idle"));

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.MoveAsync(
            id, new MoveRequest(X: 10, Y: null, Z: null, F: 3000), CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        CommandResult body = Assert.IsType<CommandResult>(conflict.Value);
        Assert.False(body.Success);
    }

    [Fact]
    public async Task HomeAsync_ReturnsConflict_WhenPrinterIsPrinting()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));

        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(c => c.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Printing"));

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.HomeAsync(id, CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        CommandResult body = Assert.IsType<CommandResult>(conflict.Value);
        Assert.False(body.Success);
        Assert.Equal("Printer is currently printing.", body.Message);
        printersService.Verify(s => s.SendHomeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HomeXYAsync_ReturnsConflict_WhenPrinterIsPrinting()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));

        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(c => c.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Printing"));

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.HomeXYAsync(id, CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        CommandResult body = Assert.IsType<CommandResult>(conflict.Value);
        Assert.False(body.Success);
        Assert.Equal("Printer is currently printing.", body.Message);
        printersService.Verify(s => s.HomeXYAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HomeZAsync_ReturnsConflict_WhenPrinterIsPrinting()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));

        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(c => c.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Printing"));

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.HomeZAsync(id, CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        CommandResult body = Assert.IsType<CommandResult>(conflict.Value);
        Assert.False(body.Success);
        Assert.Equal("Printer is currently printing.", body.Message);
        printersService.Verify(s => s.HomeZAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HomeAsync_ReturnsOk_WhenPrinterIdle()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        printersService.Setup(s => s.SendHomeAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(c => c.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Idle"));

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.HomeAsync(id, CancellationToken.None);

        CommandResult body = Assert.IsType<CommandResult>(result.Value);
        Assert.True(body.Success);
        Assert.Null(body.Message);
        printersService.Verify(s => s.SendHomeAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeAsync_AmbiguousDirectOutcome_RecordsUnknownWithoutRequiringRecovery(bool throws)
    {
        Guid id = Guid.NewGuid();
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(SamplePrinter(id));
        printers.Setup(service => service.SendHomeAsync(id, It.IsAny<CancellationToken>()))
            .Returns(() => throws ? Task.FromException<bool>(new IOException("Response lost")) : Task.FromResult(false));
        var status = new Mock<IPrinterStatusCacheReader>();
        status.Setup(cache => cache.GetStatus(id)).Returns(new PrinterStatusDto(id, true, "Idle"));
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        PrintersController controller = CreateController(printers, status, out _, actuation: actuation);

        ObjectResult response = Assert.IsType<ObjectResult>((await controller.HomeAsync(id, default)).Result);
        Assert.Equal(503, response.StatusCode);
        CommandResult result = Assert.IsType<CommandResult>(response.Value);
        Assert.False(result.Success);
        Assert.Contains("unknown", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recovery", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reconciliation", result.Message!, StringComparison.OrdinalIgnoreCase);
        actuation.Verify(service => service.MarkDirectUnknownAsync(
            It.Is<PrinterActuationLease>(lease => lease.Operation == "home" && lease.AttemptId == null),
            It.IsAny<string>(), CancellationToken.None), Times.Once);
        actuation.Verify(service => service.CompleteDirectAsync(
            It.IsAny<PrinterActuationLease>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        printers.Verify(service => service.SendHomeAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HomeAsync_DirectDeadlineExpires_CancelsSenderAndRecordsUnknownWithoutRetry()
    {
        Guid id = Guid.NewGuid();
        var clock = new DirectControlTestClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(SamplePrinter(id));
        printers.Setup(service => service.SendHomeAsync(id, It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, CancellationToken token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            });
        var status = new Mock<IPrinterStatusCacheReader>();
        status.Setup(cache => cache.GetStatus(id)).Returns(new PrinterStatusDto(id, true, "Idle"));
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        PrintersController controller = CreateController(printers, status, out _, actuation: actuation, timeProvider: clock);
        Task<ActionResult<CommandResult>> request = controller.HomeAsync(id, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.False(request.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        ObjectResult response = Assert.IsType<ObjectResult>((await request.WaitAsync(TimeSpan.FromSeconds(10))).Result);
        Assert.Equal(503, response.StatusCode);
        CommandResult body = Assert.IsType<CommandResult>(response.Value);
        Assert.False(body.Success);
        Assert.Contains("timed out", body.Message!, StringComparison.OrdinalIgnoreCase);
        actuation.Verify(service => service.MarkDirectUnknownAsync(
            It.Is<PrinterActuationLease>(lease => lease.Operation == "home" && lease.AttemptId == null),
            "backend_control_cancelled_after_send", CancellationToken.None), Times.Once);
        printers.Verify(service => service.SendHomeAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HomeAsync_DeadlineExpiresDuringPreSendValidation_DoesNotInvokeBackend()
    {
        Guid id = Guid.NewGuid();
        var clock = new DirectControlTestClock();
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(SamplePrinter(id));
        var status = new Mock<IPrinterStatusCacheReader>();
        status.Setup(cache => cache.GetStatus(id)).Returns(new PrinterStatusDto(id, true, "Idle"));
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        PrintersController controller = CreateController(printers, status, out _,
            actuation: actuation, timeProvider: clock);
        actuation.Setup(service => service.RevalidateDirectAsync(
                It.IsAny<PrinterActuationLease>(), It.IsAny<CancellationToken>()))
            .Returns((PrinterActuationLease lease, CancellationToken _) =>
            {
                clock.Advance(TimeSpan.FromMinutes(5));
                return Task.FromResult(new PrinterActuationResult(PrinterActuationResultCode.Accepted, lease));
            });
        ObjectResult response = Assert.IsType<ObjectResult>((await controller.HomeAsync(id, default)).Result);
        Assert.Equal(503, response.StatusCode);
        Assert.Equal("printer_safety_evidence_unknown", Assert.IsType<ProblemDetails>(response.Value).Extensions["code"]);
        printers.Verify(service => service.SendHomeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        actuation.Verify(service => service.CompleteDirectAsync(
            It.IsAny<PrinterActuationLease>(), false, "printer_safety_revalidation_failed",
            CancellationToken.None), Times.Once);
        actuation.Verify(service => service.CompleteDirectAsync(
            It.IsAny<PrinterActuationLease>(), It.IsAny<bool>(), "printer_operation_cancelled_before_dispatch",
            It.IsAny<CancellationToken>()), Times.Never);
        actuation.Verify(service => service.MarkDirectUnknownAsync(
            It.IsAny<PrinterActuationLease>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HomeAsync_CallerCancelsDuringPreSendValidation_PropagatesAndReleasesLease()
    {
        Guid id = Guid.NewGuid();
        using var caller = new CancellationTokenSource();
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(SamplePrinter(id));
        var status = new Mock<IPrinterStatusCacheReader>();
        status.Setup(cache => cache.GetStatus(id)).Returns(new PrinterStatusDto(id, true, "Idle"));
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        PrintersController controller = CreateController(printers, status, out _, actuation: actuation);
        actuation.Setup(service => service.RevalidateDirectAsync(
                It.IsAny<PrinterActuationLease>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterActuationLease lease, CancellationToken _) =>
            {
                caller.Cancel();
                return new PrinterActuationResult(PrinterActuationResultCode.Accepted, lease);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.HomeAsync(id, caller.Token));
        printers.Verify(service => service.SendHomeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        actuation.Verify(service => service.CompleteDirectAsync(
            It.IsAny<PrinterActuationLease>(), false, "printer_operation_cancelled_before_dispatch",
            CancellationToken.None), Times.Once);
        actuation.Verify(service => service.MarkDirectUnknownAsync(
            It.IsAny<PrinterActuationLease>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HomeXYAsync_ReturnsOk_WhenPrinterIdle()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        printersService.Setup(s => s.HomeXYAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(c => c.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Idle"));

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.HomeXYAsync(id, CancellationToken.None);

        CommandResult body = Assert.IsType<CommandResult>(result.Value);
        Assert.True(body.Success);
        Assert.Null(body.Message);
        printersService.Verify(s => s.HomeXYAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HomeZAsync_ReturnsOk_WhenPrinterIdle()
    {
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SamplePrinter(id));
        printersService.Setup(s => s.HomeZAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var statusCache = new Mock<IPrinterStatusCacheReader>();
        statusCache.Setup(c => c.GetStatus(id))
            .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Idle"));

        PrintersController controller = CreateController(printersService, statusCache, out _);

        ActionResult<CommandResult> result = await controller.HomeZAsync(id, CancellationToken.None);

        CommandResult body = Assert.IsType<CommandResult>(result.Value);
        Assert.True(body.Success);
        Assert.Null(body.Message);
        printersService.Verify(s => s.HomeZAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }
}
