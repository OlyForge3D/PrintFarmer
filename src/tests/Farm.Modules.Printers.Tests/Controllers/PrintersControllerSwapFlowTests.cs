using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Modules.Printers.Controllers;
using Farm.Modules.Printers.Controllers.Requests;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Modules.Printers.Tests.Controllers;

/// <summary>
/// Unit tests covering the API surface added for the guided filament swap flow
/// (GitHub issue OlyForge3D/PrintFarmer#710) after the Bishop/Hicks/Vasquez review blocks
/// B1 (always validate before override), B2 (never blind-bind an unmaterialized/invalid lane),
/// B6 (durable override audit context), B7 (three-state ok/mismatch/unknown wire status), plus
/// the low-severity unload 400-vs-404 mapping fix:
///   * <c>GET /api/printers/{id}/toolheads/{i}/swap-validation?spoolId=</c>
///   * <c>POST /api/printers/{id}/filament-unload</c> residual weight + failure-kind mapping
///   * <c>PUT /api/printers/{id}/toolheads/{i}/spool</c> validate-before-override + audit context
///   * Class-level <see cref="AuthorizeAttribute"/> continues to gate the new endpoint.
/// </summary>
public class PrintersControllerSwapFlowTests
{
    private static PrintersController CreateController(
        out Mock<IPrintFarmerTelemetryService> telemetry,
        Mock<IPrintersService>? printersService = null,
        Mock<IPrinterStatusCacheReader>? statusCache = null)
    {
        telemetry = new Mock<IPrintFarmerTelemetryService>();
        printersService ??= new Mock<IPrintersService>();
        statusCache ??= new Mock<IPrinterStatusCacheReader>();

        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"swap-{Guid.NewGuid():N}")
                .Options);

        var resourceAuthorization = new Mock<Farm.Infrastructure.Services.Queue.IQueueResourceAuthorizationService>();
        resourceAuthorization
            .Setup(service => service.CanAccessPrinterAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<Guid>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new PrintersController(
            logger: Mock.Of<ILogger<PrintersController>>(),
            printersService: printersService.Object,
            catalogService: Mock.Of<Farm.Modules.Printers.Services.Catalog.ICatalogService>(),
            validator: Mock.Of<IValidator<CreatePrinterFromDiscoveryDto>>(),
            discoveryProxyService: Mock.Of<Farm.Infrastructure.Services.Discovery.IDiscoveryProxyService>(),
            discoverySessions: Mock.Of<Farm.Infrastructure.Services.Discovery.IDiscoverySessionRegistry>(),
            printerBackendCapabilitiesService: Mock.Of<IPrinterBackendCapabilitiesService>(),
            backendClientFactory: Mock.Of<IBackendClientFactory>(),
            httpClientFactory: Mock.Of<IHttpClientFactory>(),
            egressGuard: Farm.Testing.Shared.AppDbTestHelpers.PermissiveEgressGuard(),
            obicoServerAssignment: Mock.Of<Farm.Infrastructure.Services.FailureDetection.IObicoServerAssignmentService>(),
            settingsService: Mock.Of<Farm.Infrastructure.Settings.ISettingsService>(),
            printerSessionTimelineService: Mock.Of<IPrinterSessionTimelineService>(),
            telemetryService: telemetry.Object,
            bedTypeService: Mock.Of<Farm.Infrastructure.Services.BedTypes.IBedTypeService>(),
            appDbContext: db,
            queueResourceAuthorization: resourceAuthorization.Object);
    }

    /// <summary>
    /// Builds an <see cref="IOperatorFeatureGate"/> mock for the guided-swap feature (#725).
    /// Defaults to enabled; pass <c>false</c> to exercise the disabled no-op path.
    /// </summary>
    private static IOperatorFeatureGate Gate(bool guidedSwapEnabled = true)
    {
        var gate = new Mock<IOperatorFeatureGate>();
        gate.Setup(g => g.IsEnabled(OperatorFeature.GuidedSwap)).Returns(guidedSwapEnabled);
        gate.Setup(g => g.IsEnabledAsync(OperatorFeature.GuidedSwap, It.IsAny<CancellationToken>())).ReturnsAsync(guidedSwapEnabled);
        gate.Setup(g => g.GetFlagName(OperatorFeature.GuidedSwap)).Returns("guidedSwapEnabled");
        return gate.Object;
    }

    /// <summary>
    /// Satisfies the <c>BindPrinterIfMatch</c> gate for <c>SetToolheadSpoolAsync</c>.
    /// Sets up <c>FindByIdAsync</c> on the mock service to return a printer with a fresh
    /// RowVersion and writes the corresponding <c>If-Match</c> header on the controller.
    /// Must be called <b>after</b> <c>controller.ControllerContext</c> is set so that
    /// <c>controller.Request</c> is available.
    /// </summary>
    private static void ArrangeSwapPrecheck(
        Guid printerId,
        Mock<IPrintersService> printers,
        PrintersController controller)
    {
        byte[] rowVersion = RevisionETag.EncodeBytes(1);
        printers
            .Setup(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer { Id = printerId, Name = "T", Revision = 1 });
        controller.Request.Headers.IfMatch = $"\"{Convert.ToBase64String(rowVersion)}\"";
    }

    /// <summary>Envelope carrying a concrete three-state validation body.</summary>
    private static SwapValidationResult Validated(
        SwapValidationStatus status,
        string? expected = null,
        string? scanned = null,
        IReadOnlyList<SwapValidationAffectedJobDto>? affected = null,
        string? reason = null) =>
        new(SwapValidationOutcome.Validated, new SwapValidationResultDto(
            status,
            expected,
            scanned,
            affected ?? Array.Empty<SwapValidationAffectedJobDto>(),
            reason));

    [Fact]
    public void PrintersController_ClassLevel_HasAuthorizeAttribute()
    {
        // Regression: [Authorize] on the controller must remain in place so all endpoints —
        // including the new swap-validation route — reject anonymous callers.
        AuthorizeAttribute? attr = typeof(PrintersController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .FirstOrDefault();
        Assert.NotNull(attr);
    }

    [Fact]
    public void GetToolheadSwapValidationAsync_IsGuardedByClassAuthorize_AndHasNoAllowAnonymous()
    {
        MethodInfo method = typeof(PrintersController).GetMethod(nameof(PrintersController.GetToolheadSwapValidationAsync))!;
        Assert.False(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: false).Any());
        // Route matches the contract required by issue #710.
        HttpGetAttribute? get = method.GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(get);
        Assert.Equal("{id:guid}/toolheads/{toolheadIndex:int}/swap-validation", get!.Template);
    }

    [Fact]
    public async Task GetToolheadSwapValidationAsync_ReturnsBadRequest_WhenSpoolIdMissing()
    {
        PrintersController controller = CreateController(out _);
        var validator = new Mock<IPrinterToolheadSwapValidator>(MockBehavior.Strict);

        ActionResult<SwapValidationResultDto> result = await controller.GetToolheadSwapValidationAsync(
            Guid.NewGuid(), 0, spoolId: null, validator.Object, Gate(), CancellationToken.None);

        BadRequestObjectResult bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        CommandResult body = Assert.IsType<CommandResult>(bad.Value);
        Assert.False(body.Success);
        validator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetToolheadSwapValidationAsync_ReturnsBadRequest_WhenToolheadIndexNegative()
    {
        PrintersController controller = CreateController(out _);
        var validator = new Mock<IPrinterToolheadSwapValidator>(MockBehavior.Strict);

        ActionResult<SwapValidationResultDto> result = await controller.GetToolheadSwapValidationAsync(
            Guid.NewGuid(), -1, spoolId: 1, validator.Object, Gate(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        validator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetToolheadSwapValidationAsync_ReturnsNotFound_WhenPrinterNotFound()
    {
        PrintersController controller = CreateController(out _);
        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapValidationResult(SwapValidationOutcome.PrinterNotFound, null));

        ActionResult<SwapValidationResultDto> result = await controller.GetToolheadSwapValidationAsync(
            Guid.NewGuid(), 0, 42, validator.Object, Gate(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetToolheadSwapValidationAsync_ReturnsNotFound_WhenToolheadNotFound()
    {
        // B2: an invalid lane (e.g., non-existent gate on a non-MMU printer) is surfaced as
        // ToolheadNotFound → 404, never a blind fall-through.
        PrintersController controller = CreateController(out _);
        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapValidationResult(SwapValidationOutcome.ToolheadNotFound, null));

        ActionResult<SwapValidationResultDto> result = await controller.GetToolheadSwapValidationAsync(
            Guid.NewGuid(), 3, 42, validator.Object, Gate(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetToolheadSwapValidationAsync_ReturnsBadRequest_WhenToolheadOutOfRange()
    {
        // B2: structurally out-of-range lane → 400.
        PrintersController controller = CreateController(out _);
        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapValidationResult(SwapValidationOutcome.ToolheadOutOfRange, null));

        ActionResult<SwapValidationResultDto> result = await controller.GetToolheadSwapValidationAsync(
            Guid.NewGuid(), 99, 42, validator.Object, Gate(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetToolheadSwapValidationAsync_ReturnsTypedResult_WhenValidatorSucceeds()
    {
        Guid printerId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        SwapValidationResult expected = Validated(
            SwapValidationStatus.Mismatch,
            expected: "PLA",
            scanned: "PETG",
            affected: new[]
            {
                new SwapValidationAffectedJobDto(jobId, "cube", PrintJobStatus.Queued, 0, "PLA"),
            },
            reason: "Scanned material 'PETG' does not match expected 'PLA'.");

        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 0, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        PrintersController controller = CreateController(out Mock<IPrintFarmerTelemetryService> telemetry);

        ActionResult<SwapValidationResultDto> result = await controller.GetToolheadSwapValidationAsync(
            printerId, 0, 42, validator.Object, Gate(), CancellationToken.None);

        SwapValidationResultDto body = Assert.IsType<SwapValidationResultDto>(result.Value);
        Assert.Same(expected.Result, body);
        Assert.Equal(SwapValidationStatus.Mismatch, body.Status);
        // A mismatch is not "ok", so the swap_validation success flag is false.
        telemetry.Verify(t => t.RecordPrinterOperation("swap_validation", printerId.ToString(), false), Times.Once);
    }

    [Theory]
    [InlineData(SwapValidationStatus.Ok, "\"status\":\"ok\"")]
    [InlineData(SwapValidationStatus.Mismatch, "\"status\":\"mismatch\"")]
    [InlineData(SwapValidationStatus.Unknown, "\"status\":\"unknown\"")]
    public void SwapValidationResultDto_SerializesStatusAsLowercaseWireToken(SwapValidationStatus status, string expectedFragment)
    {
        // B7: the three-state status must serialize to the exact lowercase wire tokens
        // ok / mismatch / unknown, WITHOUT changing the global PascalCase enum policy.
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverterAlias());

        var dto = new SwapValidationResultDto(
            status,
            Expected: "PLA",
            Scanned: "PETG",
            AffectedJobs: new[]
            {
                new SwapValidationAffectedJobDto(Guid.Empty, "j", PrintJobStatus.Assigned, 1, "PETG"),
            },
            Reason: "no");

        string json = JsonSerializer.Serialize(dto, options);

        Assert.Contains(expectedFragment, json);
        Assert.Contains("\"expected\":\"PLA\"", json);
        Assert.Contains("\"scanned\":\"PETG\"", json);
        Assert.Contains("\"affectedJobs\":[", json);
        Assert.Contains("\"reason\":\"no\"", json);
        Assert.Contains("\"tool\":1", json);
        Assert.Contains("\"expectedMaterial\":\"PETG\"", json);
        // Affected-job status still uses the global PascalCase enum policy — feature-local
        // converter must not leak into other enums.
        Assert.Contains("\"status\":\"Assigned\"", json);
        Assert.DoesNotContain("\"Ok\"", json);
    }

    [Fact]
    public async Task UnloadFilamentAsync_ReturnsResidualWeight_OnSuccess()
    {
        Guid id = Guid.NewGuid();
        var expected = new FilamentUnloadResult(
            Success: true,
            Message: "Filament unload initiated",
            SpoolId: 17,
            Material: "PLA",
            ResidualWeightG: 234.5);

        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.UnloadFilamentAsync(id, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        PrintersController controller = CreateController(out _, printersService, statusCache: null);

        ActionResult<FilamentUnloadResult> result = await controller.UnloadFilamentAsync(id, toolheadIndex: null, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        FilamentUnloadResult body = Assert.IsType<FilamentUnloadResult>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal(17, body.SpoolId);
        Assert.Equal("PLA", body.Material);
        Assert.Equal(234.5, body.ResidualWeightG);
    }

    [Fact]
    public async Task UnloadFilamentAsync_ReturnsNotFound_WhenPrinterMissing()
    {
        // Low-fix: printer-not-found maps via the typed FailureKind (PrinterNotFound) → 404,
        // not by brittle message substring matching.
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.UnloadFilamentAsync(id, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilamentUnloadResult(
                false,
                "no such printer",
                FailureKind: FilamentUnloadFailureKind.PrinterNotFound));

        PrintersController controller = CreateController(out _, printersService, statusCache: null);

        ActionResult<FilamentUnloadResult> result = await controller.UnloadFilamentAsync(id, toolheadIndex: null, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task UnloadFilamentAsync_ReturnsBadRequest_WhenInvalidToolhead()
    {
        // Low-fix: an invalid toolhead index maps to a documented 400 (InvalidToolhead),
        // distinct from the 404 printer-not-found path — even though the message also
        // contains "not found".
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.UnloadFilamentAsync(id, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilamentUnloadResult(
                false,
                $"Toolhead index 5 not found on printer X",
                FailureKind: FilamentUnloadFailureKind.InvalidToolhead));

        PrintersController controller = CreateController(out _, printersService, statusCache: null);

        ActionResult<FilamentUnloadResult> result = await controller.UnloadFilamentAsync(id, toolheadIndex: 5, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UnloadFilamentAsync_ForwardsExplicitToolheadIndex_ToService()
    {
        Guid id = Guid.NewGuid();
        var expected = new FilamentUnloadResult(true, "ok", SpoolId: 8, Material: "PETG", ResidualWeightG: 111);

        var printersService = new Mock<IPrintersService>(MockBehavior.Strict);
        printersService.Setup(s => s.UnloadFilamentAsync(id, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected)
            .Verifiable();

        PrintersController controller = CreateController(out _, printersService, statusCache: null);

        ActionResult<FilamentUnloadResult> result = await controller.UnloadFilamentAsync(id, toolheadIndex: 2, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        printersService.Verify();
    }

    [Fact]
    public void FilamentUnloadResult_SerializesCamelCaseIncludingResidualWeight()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dto = new FilamentUnloadResult(true, "ok", 5, "PLA", 123.4);

        string json = JsonSerializer.Serialize(dto, options);

        Assert.Contains("\"success\":true", json);
        Assert.Contains("\"message\":\"ok\"", json);
        Assert.Contains("\"spoolId\":5", json);
        Assert.Contains("\"material\":\"PLA\"", json);
        Assert.Contains("\"residualWeightG\":123.4", json);
        // FailureKind is a server-only discriminator and must not leak onto the wire.
        Assert.DoesNotContain("failureKind", json);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_ValidatesBeforeOverride_AndBindsWithAuditContext_OnMismatchOverride()
    {
        // B1: even with a valid override flag + reason, the controller MUST validate first.
        // B6: on a genuine mismatch override the service is invoked WITH a non-null audit
        // context so the durable audit row commits atomically with the binding.
        Guid printerId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();

        FilamentSwapOverrideContext? captured = null;
        var printersService = new Mock<IPrintersService>();
        printersService
            .Setup(s => s.SetToolheadSpoolAsync(printerId, 0, 42, It.IsAny<FilamentSwapOverrideContext?>(), It.IsAny<SpoolBindPolicy>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, int, int, FilamentSwapOverrideContext?, SpoolBindPolicy, CancellationToken>((_, _, _, ctx, _, _) => captured = ctx)
            .ReturnsAsync(new CommandResult(true, "Spool 42 assigned to toolhead T0"));

        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 0, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Validated(
                SwapValidationStatus.Mismatch,
                expected: "PLA",
                scanned: "PETG",
                affected: new[] { new SwapValidationAffectedJobDto(jobId, "cube", PrintJobStatus.Queued, 0, "PLA") },
                reason: "mismatch"));

        PrintersController controller = CreateController(out Mock<IPrintFarmerTelemetryService> telemetry, printersService, statusCache: null);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-123"),
                    new Claim(ClaimTypes.Name, "operator-1"),
                }, "test")),
            },
        };
        ArrangeSwapPrecheck(printerId, printersService, controller);

        var request = new SetActiveSpoolRequest
        {
            SpoolId = 42,
            OverrideMismatch = true,
            OverrideReason = "operator confirmed material substitution",
        };

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 0, request, validator.Object, Gate(), CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(Assert.IsType<CommandResult>(ok.Value).Success);

        // Validator WAS consulted before the bind (B1).
        validator.Verify(v => v.ValidateAsync(printerId, 0, 42, It.IsAny<CancellationToken>()), Times.Once);

        // Audit context captured with authenticated identity + affected jobs (B6).
        Assert.NotNull(captured);
        Assert.Equal("user-123", captured!.UserId);
        Assert.Equal("operator-1", captured.UserName);
        Assert.Equal("operator confirmed material substitution", captured.Reason);
        Assert.Equal("PLA", captured.ExpectedMaterial);
        Assert.Equal("PETG", captured.ScannedMaterial);
        Assert.Equal(new[] { jobId }, captured.AffectedJobIds);

        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool_override", printerId.ToString(), true), Times.Once);
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool", printerId.ToString(), true), Times.Once);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_BindsWithoutAudit_WhenValidatorReportsOk()
    {
        // Status ok → normal write, no override context, no override telemetry.
        Guid printerId = Guid.NewGuid();

        FilamentSwapOverrideContext? captured = new("x", "y", "z", null, null, Array.Empty<Guid>());
        SpoolBindPolicy capturedPolicy = SpoolBindPolicy.Direct;
        var printersService = new Mock<IPrintersService>();
        printersService
            .Setup(s => s.SetToolheadSpoolAsync(printerId, 0, 42, It.IsAny<FilamentSwapOverrideContext?>(), It.IsAny<SpoolBindPolicy>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, int, int, FilamentSwapOverrideContext?, SpoolBindPolicy, CancellationToken>((_, _, _, ctx, policy, _) => { captured = ctx; capturedPolicy = policy; })
            .ReturnsAsync(new CommandResult(true, "ok"));

        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 0, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Validated(SwapValidationStatus.Ok, expected: "PLA", scanned: "PLA"));

        PrintersController controller = CreateController(out Mock<IPrintFarmerTelemetryService> telemetry, printersService, statusCache: null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        ArrangeSwapPrecheck(printerId, printersService, controller);

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 0, new SetActiveSpoolRequest { SpoolId = 42 }, validator.Object, Gate(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Null(captured); // no audit context on the ok path
        Assert.Equal(SpoolBindPolicy.Guided, capturedPolicy); // C1: guided bind policy when the feature is on
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool_override", It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_RejectsWithConflict_WhenValidatorReportsMismatchAndNoOverride()
    {
        Guid printerId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        SwapValidationResult mismatch = Validated(
            SwapValidationStatus.Mismatch,
            expected: "PLA",
            scanned: "PETG",
            affected: new[] { new SwapValidationAffectedJobDto(jobId, "cube", PrintJobStatus.Queued, 0, "PLA") },
            reason: "Scanned material 'PETG' does not match expected 'PLA'.");

        // The PrintersService is strict: the controller MUST NOT bind when a hard-stop
        // mismatch fires without an override.
        var printersService = new Mock<IPrintersService>(MockBehavior.Strict);

        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 0, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mismatch);

        PrintersController controller = CreateController(out Mock<IPrintFarmerTelemetryService> telemetry, printersService, statusCache: null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        ArrangeSwapPrecheck(printerId, printersService, controller);

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 0, new SetActiveSpoolRequest { SpoolId = 42 }, validator.Object, Gate(), CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        SwapValidationResultDto body = Assert.IsType<SwapValidationResultDto>(conflict.Value);
        Assert.Equal(SwapValidationStatus.Mismatch, body.Status);
        Assert.Equal("PLA", body.Expected);
        Assert.Equal("PETG", body.Scanned);

        // Server-enforced hard stop: no bind, no telemetry at all.
        printersService.Verify(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()), Times.Once);
        printersService.VerifyNoOtherCalls();
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool", It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool_override", It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_RejectsWithConflict_WhenValidatorReportsUnknown_EvenWithOverride()
    {
        // B7: an unknown status must NEVER be overridden — no bind, no audit — even when the
        // request carries a valid override flag + reason.
        Guid printerId = Guid.NewGuid();

        var printersService = new Mock<IPrintersService>(MockBehavior.Strict);
        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 0, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Validated(SwapValidationStatus.Unknown, expected: "PLA", scanned: null, reason: "spool unresolved"));

        PrintersController controller = CreateController(out Mock<IPrintFarmerTelemetryService> telemetry, printersService, statusCache: null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        ArrangeSwapPrecheck(printerId, printersService, controller);

        var request = new SetActiveSpoolRequest
        {
            SpoolId = 42,
            OverrideMismatch = true,
            OverrideReason = "forcing it",
        };

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 0, request, validator.Object, Gate(), CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(SwapValidationStatus.Unknown, Assert.IsType<SwapValidationResultDto>(conflict.Value).Status);
        printersService.Verify(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()), Times.Once);
        printersService.VerifyNoOtherCalls();
        telemetry.Verify(t => t.RecordPrinterOperation(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_OverrideMismatch_WriteFails_NoAuditTelemetry()
    {
        // B6: when the bind fails downstream, the audit warning + override telemetry must NOT
        // fire. (The durable audit row is staged inside the service's unit of work, so a
        // failed SaveChanges rolls it back atomically.)
        Guid printerId = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService
            .Setup(s => s.SetToolheadSpoolAsync(printerId, 1, 42, It.IsAny<FilamentSwapOverrideContext?>(), It.IsAny<SpoolBindPolicy>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(false, "Backend rejected assignment"));

        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 1, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Validated(SwapValidationStatus.Mismatch, expected: "PLA", scanned: "PETG", reason: "mismatch"));

        PrintersController controller = CreateController(out Mock<IPrintFarmerTelemetryService> telemetry, printersService, statusCache: null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        ArrangeSwapPrecheck(printerId, printersService, controller);

        var request = new SetActiveSpoolRequest
        {
            SpoolId = 42,
            OverrideMismatch = true,
            OverrideReason = "forcing PETG onto T1",
        };

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 1, request, validator.Object, Gate(), CancellationToken.None);

        BadRequestObjectResult bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(Assert.IsType<CommandResult>(bad.Value).Success);
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool", printerId.ToString(), false), Times.Once);
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool_override", It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_ConcurrentGateMaterializationConflict_Returns409()
    {
        Guid printerId = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService
            .Setup(s => s.SetToolheadSpoolAsync(
                printerId,
                1,
                42,
                It.IsAny<FilamentSwapOverrideContext?>(),
                SpoolBindPolicy.Guided,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolheadSpoolBindResult(
                false,
                "Toolhead T1 was created by another request; retry the spool assignment",
                ToolheadSpoolBindFailureKind.TopologyConflict));
        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 1, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Validated(SwapValidationStatus.Ok, expected: "PLA", scanned: "PLA"));
        PrintersController controller = CreateController(
            out Mock<IPrintFarmerTelemetryService> telemetry,
            printersService,
            statusCache: null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        ArrangeSwapPrecheck(printerId, printersService, controller);

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId,
            1,
            new SetActiveSpoolRequest { SpoolId = 42 },
            validator.Object,
            Gate(),
            CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        ToolheadSpoolBindResult body = Assert.IsType<ToolheadSpoolBindResult>(conflict.Value);
        Assert.Equal(ToolheadSpoolBindFailureKind.TopologyConflict, body.FailureKind);
        telemetry.Verify(
            t => t.RecordPrinterOperation("set_toolhead_spool", printerId.ToString(), false),
            Times.Once);
        telemetry.Verify(
            t => t.RecordPrinterOperation("set_toolhead_spool_override", It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_ReturnsNotFound_WhenValidatorReportsPrinterNotFound_NoBind()
    {
        // B2: a printer-not-found outcome from the validator must return 404 and NEVER fall
        // through to a blind bind.
        Guid printerId = Guid.NewGuid();
        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 0, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapValidationResult(SwapValidationOutcome.PrinterNotFound, null));

        var printersService = new Mock<IPrintersService>(MockBehavior.Strict);

        PrintersController controller = CreateController(out _, printersService, statusCache: null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        ArrangeSwapPrecheck(printerId, printersService, controller);

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 0, new SetActiveSpoolRequest { SpoolId = 42 }, validator.Object, Gate(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        printersService.Verify(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()), Times.Once);
        printersService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_ReturnsNotFound_WhenValidatorReportsToolheadNotFound_NoBind()
    {
        // B2: an invalid/unmaterialized lane that is not a valid filament source → 404, no bind.
        Guid printerId = Guid.NewGuid();
        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 4, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapValidationResult(SwapValidationOutcome.ToolheadNotFound, null));

        var printersService = new Mock<IPrintersService>(MockBehavior.Strict);

        PrintersController controller = CreateController(out _, printersService, statusCache: null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        ArrangeSwapPrecheck(printerId, printersService, controller);

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 4, new SetActiveSpoolRequest { SpoolId = 42 }, validator.Object, Gate(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        printersService.Verify(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()), Times.Once);
        printersService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_ReturnsBadRequest_WhenValidatorReportsOutOfRange_NoBind()
    {
        // B2: structurally out-of-range lane → 400, no bind.
        Guid printerId = Guid.NewGuid();
        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 99, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapValidationResult(SwapValidationOutcome.ToolheadOutOfRange, null));

        var printersService = new Mock<IPrintersService>(MockBehavior.Strict);

        PrintersController controller = CreateController(out _, printersService, statusCache: null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        ArrangeSwapPrecheck(printerId, printersService, controller);

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 99, new SetActiveSpoolRequest { SpoolId = 42 }, validator.Object, Gate(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        printersService.Verify(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()), Times.Once);
        printersService.VerifyNoOtherCalls();
    }

    [Fact]
    public void SetActiveSpoolRequest_DeserializesOverrideFields_FromCamelCaseJson()
    {
        const string json = "{\"spoolId\":9,\"overrideMismatch\":true,\"overrideReason\":\"loaded PLA over PETG\"}";

        SetActiveSpoolRequest? request = JsonSerializer.Deserialize<SetActiveSpoolRequest>(json);

        Assert.NotNull(request);
        Assert.Equal(9, request!.SpoolId);
        Assert.True(request.OverrideMismatch);
        Assert.Equal("loaded PLA over PETG", request.OverrideReason);
    }

    [Fact]
    public void ToolheadSpoolBindResult_DoesNotSerializeFailureKind()
    {
        var result = new ToolheadSpoolBindResult(
            false,
            "retry",
            ToolheadSpoolBindFailureKind.TopologyConflict);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        string json = JsonSerializer.Serialize(result, options);

        Assert.Contains("\"success\":false", json);
        Assert.Contains("\"message\":\"retry\"", json);
        Assert.DoesNotContain("failureKind", json);
        Assert.DoesNotContain("topologyConflict", json);
    }

    [Fact]
    public async Task GetToolheadSwapValidationAsync_ReturnsFeatureDisabled404_WhenGuidedSwapDisabled()
    {
        // #725 gate: when guidedSwapEnabled is off the endpoint must short-circuit to the
        // standard featureDisabled 404 ProblemDetails BEFORE any validator read or telemetry.
        var validator = new Mock<IPrinterToolheadSwapValidator>(MockBehavior.Strict);

        PrintersController controller = CreateController(out Mock<IPrintFarmerTelemetryService> telemetry);

        ActionResult<SwapValidationResultDto> result = await controller.GetToolheadSwapValidationAsync(
            Guid.NewGuid(), 0, spoolId: 42, validator.Object, Gate(guidedSwapEnabled: false), CancellationToken.None);

        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        ProblemDetails problem = Assert.IsType<ProblemDetails>(notFound.Value);
        Assert.Equal(404, problem.Status);
        Assert.Equal("featureDisabled", problem.Extensions["code"]);
        Assert.Equal("guidedSwapEnabled", problem.Extensions["feature"]);
        validator.VerifyNoOtherCalls();
        telemetry.Verify(
            t => t.RecordPrinterOperation("swap_validation", It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_SkipsValidationAndAudit_WhenGuidedSwapDisabled()
    {
        // #725 gate: when guidedSwapEnabled is off the spool-binding control REMAINS available
        // (direct capability-gated control), but reverts to pre-#710 blind assignment: no
        // pre-flight validation, no override audit. Strict validator proves it is never called
        // even though the request carries a valid override. The service is invoked with a NULL
        // audit context.
        Guid printerId = Guid.NewGuid();
        FilamentSwapOverrideContext? captured = new("x", "y", "z", null, null, Array.Empty<Guid>());
        SpoolBindPolicy capturedPolicy = SpoolBindPolicy.Guided;
        var printersService = new Mock<IPrintersService>();
        printersService
            .Setup(s => s.SetToolheadSpoolAsync(printerId, 0, 42, It.IsAny<FilamentSwapOverrideContext?>(), It.IsAny<SpoolBindPolicy>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, int, int, FilamentSwapOverrideContext?, SpoolBindPolicy, CancellationToken>((_, _, _, ctx, policy, _) => { captured = ctx; capturedPolicy = policy; })
            .ReturnsAsync(new CommandResult(true, "assigned"));

        var validator = new Mock<IPrinterToolheadSwapValidator>(MockBehavior.Strict);

        PrintersController controller = CreateController(out Mock<IPrintFarmerTelemetryService> telemetry, printersService, statusCache: null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        ArrangeSwapPrecheck(printerId, printersService, controller);

        var request = new SetActiveSpoolRequest
        {
            SpoolId = 42,
            OverrideMismatch = true,
            OverrideReason = "would-be override, but feature disabled",
        };

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 0, request, validator.Object, Gate(guidedSwapEnabled: false), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Null(captured); // no audit context when the feature is disabled
        Assert.Equal(SpoolBindPolicy.Direct, capturedPolicy); // C1: direct bind policy preserves pre-#710 behavior
        validator.VerifyNoOtherCalls();
        // Assignment telemetry still fires; override audit telemetry must NOT.
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool", printerId.ToString(), true), Times.Once);
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool_override", It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_OverrideWithoutReason_StillValidates_AndConflictsOnMismatch()
    {
        // Issue #710 contract: an override flag WITHOUT a non-empty reason is not a valid
        // override. Validation still runs, so a genuine mismatch is rejected with 409 until
        // the operator supplies a reason. Guards against bypassing the hard-stop by setting the
        // flag alone.
        Guid printerId = Guid.NewGuid();
        SwapValidationResult mismatch = Validated(
            SwapValidationStatus.Mismatch,
            expected: "PLA",
            scanned: "PETG",
            reason: "Scanned material 'PETG' does not match expected 'PLA'.");

        var printersService = new Mock<IPrintersService>(MockBehavior.Strict);
        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 0, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mismatch);

        PrintersController controller = CreateController(out Mock<IPrintFarmerTelemetryService> telemetry, printersService, statusCache: null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        ArrangeSwapPrecheck(printerId, printersService, controller);

        var request = new SetActiveSpoolRequest
        {
            SpoolId = 42,
            OverrideMismatch = true,
            OverrideReason = "   ", // whitespace-only: not a valid reason
        };

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 0, request, validator.Object, Gate(), CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(SwapValidationStatus.Mismatch, Assert.IsType<SwapValidationResultDto>(conflict.Value).Status);
        printersService.Verify(s => s.FindByIdAsync(printerId, It.IsAny<CancellationToken>()), Times.Once);
        printersService.VerifyNoOtherCalls();
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool_override", It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    // Local alias so the test file avoids a namespace clash with Farm.Infrastructure enum converters.
    // S2094 false positive: intentionally empty — this exists solely to give a distinct type name
    // to Json.Serialization.JsonStringEnumConverter, it deliberately adds no members.
#pragma warning disable S2094
    private sealed class JsonStringEnumConverterAlias : System.Text.Json.Serialization.JsonStringEnumConverter
    {
    }
#pragma warning restore S2094
}
