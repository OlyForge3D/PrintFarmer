using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Requests;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Unit tests covering the API surface added for the guided filament swap flow
/// (GitHub issue OlyForge3D/PrintFarmer#710):
///   * <c>GET /api/printers/{id}/toolheads/{i}/swap-validation?spoolId=</c>
///   * <c>POST /api/printers/{id}/filament-unload</c> residual weight
///   * <c>PUT /api/printers/{id}/toolheads/{i}/spool</c> override intent
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

        return new PrintersController(
            logger: Mock.Of<ILogger<PrintersController>>(),
            printersService: printersService.Object,
            catalogService: Mock.Of<Farm.Web.Api.Services.Catalog.ICatalogService>(),
            validator: Mock.Of<IValidator<CreatePrinterFromDiscoveryDto>>(),
            discoveryProxyService: Mock.Of<Farm.Infrastructure.Services.Discovery.IDiscoveryProxyService>(),
            printerBackendCapabilitiesService: Mock.Of<IPrinterBackendCapabilitiesService>(),
            backendClientFactory: Mock.Of<IBackendClientFactory>(),
            httpClientFactory: Mock.Of<IHttpClientFactory>(),
            obicoServerAssignment: Mock.Of<Farm.Infrastructure.Services.FailureDetection.IObicoServerAssignmentService>(),
            settingsService: Mock.Of<Farm.Infrastructure.Settings.ISettingsService>(),
            printerSessionTimelineService: Mock.Of<IPrinterSessionTimelineService>(),
            telemetryService: telemetry.Object,
            bedTypeService: Mock.Of<Farm.Infrastructure.Services.BedTypes.IBedTypeService>(),
            printerStatusCache: statusCache.Object);
    }

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
            Guid.NewGuid(), 0, spoolId: null, validator.Object, CancellationToken.None);

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
            Guid.NewGuid(), -1, spoolId: 1, validator.Object, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        validator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetToolheadSwapValidationAsync_ReturnsNotFound_WhenValidatorReturnsNull()
    {
        PrintersController controller = CreateController(out _);
        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SwapValidationResultDto?)null);

        ActionResult<SwapValidationResultDto> result = await controller.GetToolheadSwapValidationAsync(
            Guid.NewGuid(), 0, 42, validator.Object, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetToolheadSwapValidationAsync_ReturnsTypedResult_WhenValidatorSucceeds()
    {
        Guid printerId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        var expected = new SwapValidationResultDto(
            Ok: false,
            Expected: "PLA",
            Scanned: "PETG",
            AffectedJobs: new[]
            {
                new SwapValidationAffectedJobDto(jobId, "cube", PrintJobStatus.Queued, 0, "PLA"),
            },
            Reason: "Scanned material 'PETG' does not match expected 'PLA'.");

        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 0, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        PrintersController controller = CreateController(out Mock<IPrintFarmerTelemetryService> telemetry);

        ActionResult<SwapValidationResultDto> result = await controller.GetToolheadSwapValidationAsync(
            printerId, 0, 42, validator.Object, CancellationToken.None);

        SwapValidationResultDto body = Assert.IsType<SwapValidationResultDto>(result.Value);
        Assert.Same(expected, body);
        telemetry.Verify(t => t.RecordPrinterOperation("swap_validation", printerId.ToString(), false), Times.Once);
    }

    [Fact]
    public void SwapValidationResultDto_SerializesUsingCamelCaseFieldNames()
    {
        // API + SignalR contract: all payloads use camelCase and enums serialize as strings.
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverterAlias());

        var dto = new SwapValidationResultDto(
            Ok: false,
            Expected: "PLA",
            Scanned: "PETG",
            AffectedJobs: new[]
            {
                new SwapValidationAffectedJobDto(Guid.Empty, "j", PrintJobStatus.Assigned, 1, "PETG"),
            },
            Reason: "no");

        string json = JsonSerializer.Serialize(dto, options);

        Assert.Contains("\"ok\":false", json);
        Assert.Contains("\"expected\":\"PLA\"", json);
        Assert.Contains("\"scanned\":\"PETG\"", json);
        Assert.Contains("\"affectedJobs\":[", json);
        Assert.Contains("\"reason\":\"no\"", json);
        Assert.Contains("\"tool\":1", json);
        Assert.Contains("\"expectedMaterial\":\"PETG\"", json);
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
        Guid id = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.UnloadFilamentAsync(id, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilamentUnloadResult(false, $"Printer {id} not found"));

        PrintersController controller = CreateController(out _, printersService, statusCache: null);

        ActionResult<FilamentUnloadResult> result = await controller.UnloadFilamentAsync(id, toolheadIndex: null, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
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
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_RecordsOverrideTelemetry_WhenOverrideMismatchTrue()
    {
        Guid printerId = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.SetToolheadSpoolAsync(printerId, 0, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(true, "Spool 42 assigned to toolhead T0"));

        PrintersController controller = CreateController(out Mock<IPrintFarmerTelemetryService> telemetry, printersService, statusCache: null);
        // Supply a user principal so the override log line captures a stable identifier.
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "operator-1") }, "test")),
            }
        };

        var request = new SetActiveSpoolRequest
        {
            SpoolId = 42,
            OverrideMismatch = true,
            OverrideReason = "operator confirmed material substitution",
        };

        // Override path skips the pre-flight validator entirely — the strict mock
        // enforces that the controller never calls ValidateAsync when override=true.
        var validator = new Mock<IPrinterToolheadSwapValidator>(MockBehavior.Strict);

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 0, request, validator.Object, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(Assert.IsType<CommandResult>(ok.Value).Success);
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool_override", printerId.ToString(), true), Times.Once);
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool", printerId.ToString(), true), Times.Once);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_DoesNotRecordOverrideTelemetry_WhenOverrideMismatchFalse()
    {
        Guid printerId = Guid.NewGuid();
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.SetToolheadSpoolAsync(printerId, 0, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(true, "ok"));

        // Validator says the material matches — assignment proceeds normally and no
        // override telemetry fires.
        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 0, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapValidationResultDto(
                Ok: true,
                Expected: "PLA",
                Scanned: "PLA",
                AffectedJobs: Array.Empty<SwapValidationAffectedJobDto>()));

        PrintersController controller = CreateController(out Mock<IPrintFarmerTelemetryService> telemetry, printersService, statusCache: null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 0, new SetActiveSpoolRequest { SpoolId = 42 }, validator.Object, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool_override", It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_RejectsWithConflict_WhenValidatorReportsMismatchAndNoOverride()
    {
        Guid printerId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        var mismatch = new SwapValidationResultDto(
            Ok: false,
            Expected: "PLA",
            Scanned: "PETG",
            AffectedJobs: new[]
            {
                new SwapValidationAffectedJobDto(jobId, "cube", PrintJobStatus.Queued, 0, "PLA"),
            },
            Reason: "Scanned material 'PETG' does not match expected 'PLA'.");

        // The PrintersService is strict: the controller MUST NOT call SetToolheadSpoolAsync
        // when a hard-stop mismatch fires without an override.
        var printersService = new Mock<IPrintersService>(MockBehavior.Strict);

        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 0, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mismatch);

        PrintersController controller = CreateController(out Mock<IPrintFarmerTelemetryService> telemetry, printersService, statusCache: null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 0, new SetActiveSpoolRequest { SpoolId = 42 }, validator.Object, CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        SwapValidationResultDto body = Assert.IsType<SwapValidationResultDto>(conflict.Value);
        Assert.False(body.Ok);
        Assert.Equal("PLA", body.Expected);
        Assert.Equal("PETG", body.Scanned);

        // Server-enforced hard stop: no assignment call, no set_toolhead_spool telemetry,
        // and — critically — no override telemetry either.
        printersService.VerifyNoOtherCalls();
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool", It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool_override", It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_OverrideMismatch_WriteFails_NoAuditEmitted()
    {
        Guid printerId = Guid.NewGuid();
        // Assignment fails downstream — the audit warning + override telemetry must NOT
        // fire because the write never landed. Also verify validator is not consulted
        // when override=true.
        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.SetToolheadSpoolAsync(printerId, 1, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(false, "Backend rejected assignment"));

        var validator = new Mock<IPrinterToolheadSwapValidator>(MockBehavior.Strict);

        PrintersController controller = CreateController(out Mock<IPrintFarmerTelemetryService> telemetry, printersService, statusCache: null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var request = new SetActiveSpoolRequest
        {
            SpoolId = 42,
            OverrideMismatch = true,
            OverrideReason = "forcing PETG onto T1",
        };

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 1, request, validator.Object, CancellationToken.None);

        BadRequestObjectResult bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(Assert.IsType<CommandResult>(bad.Value).Success);
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool", printerId.ToString(), false), Times.Once);
        telemetry.Verify(t => t.RecordPrinterOperation("set_toolhead_spool_override", It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_ProceedsWhenValidatorReturnsNull_LetsDownstreamMap404()
    {
        Guid printerId = Guid.NewGuid();
        // Validator returns null (printer/toolhead not found by the pre-flight lookup).
        // The controller must not swallow this into a 200/409 — it must fall through so
        // the downstream service's own 404 mapping stays authoritative.
        var validator = new Mock<IPrinterToolheadSwapValidator>();
        validator.Setup(v => v.ValidateAsync(printerId, 0, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SwapValidationResultDto?)null);

        var printersService = new Mock<IPrintersService>();
        printersService.Setup(s => s.SetToolheadSpoolAsync(printerId, 0, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(false, $"Printer {printerId} not found"));

        PrintersController controller = CreateController(out _, printersService, statusCache: null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        ActionResult<CommandResult> result = await controller.SetToolheadSpoolAsync(
            printerId, 0, new SetActiveSpoolRequest { SpoolId = 42 }, validator.Object, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
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

    // Local alias so the test file avoids a namespace clash with Farm.Infrastructure enum converters.
    private sealed class JsonStringEnumConverterAlias : System.Text.Json.Serialization.JsonStringEnumConverter
    {
    }
}
