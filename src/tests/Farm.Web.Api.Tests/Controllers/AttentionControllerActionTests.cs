using System.Security.Claims;
using System.Text.Json;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Web.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class AttentionControllerActionTests
{
    [Fact]
    public async Task ExecuteActionAsync_HarvestMappingRequired_ReturnsCanonicalProblemDetails()
    {
        Guid jobId = Guid.NewGuid();
        Guid projectFileId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();
        var details = new PartMappingRequiredResponse(
            jobId,
            projectFileId,
            gcodeFileId,
            "Configure a mapping or supply outputs.");
        (AttentionController controller, Mock<IAttentionService> service, Mock<IAttentionBroadcaster> broadcaster) =
            CreateController(harvestAction: true);
        service.Setup(value => value.ExecuteActionAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                $"harvest:{jobId}",
                AttentionActionKind.Harvest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AttentionActionResult(
                AttentionActionOutcome.Conflict,
                "mapping required",
                new AttentionPartMappingRequiredProblem(details)));

        IActionResult result = await controller.ExecuteActionAsync(
            $"harvest:{jobId}",
            AttentionActionKind.Harvest,
            CancellationToken.None);

        ObjectResult conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Contains("application/problem+json", conflict.ContentTypes);
        ProblemDetails problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal("partMappingRequired", problem.Extensions["code"]);
        Assert.Equal(jobId, problem.Extensions["jobId"]);
        Assert.Equal(projectFileId, problem.Extensions["projectFileId"]);
        Assert.Equal(gcodeFileId, problem.Extensions["gcodeFileId"]);
        Assert.Equal(details.Guidance, problem.Extensions["guidance"]);
        service.VerifyAll();
        broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteActionAsync_HarvestWrongBin_ReturnsCanonicalProblemDetails()
    {
        Guid jobId = Guid.NewGuid();
        var details = new WrongBinResponse(
        [
            new WrongBinMismatchResponse("SKU-A", "BIN-A", "BIN-B"),
        ]);
        (AttentionController controller, Mock<IAttentionService> service, Mock<IAttentionBroadcaster> broadcaster) =
            CreateController(harvestAction: true);
        service.Setup(value => value.ExecuteActionAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                $"harvest:{jobId}",
                AttentionActionKind.Harvest,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AttentionActionResult(
                AttentionActionOutcome.Conflict,
                "wrong bin",
                new AttentionWrongBinProblem(details)));

        IActionResult result = await controller.ExecuteActionAsync(
            $"harvest:{jobId}",
            AttentionActionKind.Harvest,
            CancellationToken.None);

        ObjectResult conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Contains("application/problem+json", conflict.ContentTypes);
        ProblemDetails problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal("wrongBin", problem.Extensions["code"]);
        IReadOnlyList<WrongBinMismatchResponse> mismatches =
            Assert.IsAssignableFrom<IReadOnlyList<WrongBinMismatchResponse>>(
                problem.Extensions["mismatches"]);
        WrongBinMismatchResponse mismatch = Assert.Single(mismatches);
        Assert.Equal("SKU-A", mismatch.PartSku);
        Assert.Equal("BIN-A", mismatch.ExpectedBinCode);
        Assert.Equal("BIN-B", mismatch.ScannedBinCode);
        string json = JsonSerializer.Serialize(problem, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        Assert.Contains("\"partSku\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expectedBinCode\"", json, StringComparison.Ordinal);
        Assert.Contains("\"scannedBinCode\"", json, StringComparison.Ordinal);
        service.VerifyAll();
        broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteActionAsync_GenericConflict_RetainsErrorEnvelope()
    {
        (AttentionController controller, Mock<IAttentionService> service, Mock<IAttentionBroadcaster> broadcaster) =
            CreateController(harvestAction: false);
        service.Setup(value => value.ExecuteActionAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                "failure:1",
                AttentionActionKind.Pause,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AttentionActionResult(AttentionActionOutcome.Conflict, "printer busy"));

        IActionResult result = await controller.ExecuteActionAsync(
            "failure:1",
            AttentionActionKind.Pause,
            CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        Assert.Equal("printer busy", json.RootElement.GetProperty("error").GetString());
        service.VerifyAll();
        broadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteActionAsync_Success_RetainsOutcomeEnvelope()
    {
        (AttentionController controller, Mock<IAttentionService> service, Mock<IAttentionBroadcaster> broadcaster) =
            CreateController(harvestAction: false);
        service.Setup(value => value.ExecuteActionAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                "maintenance:1",
                AttentionActionKind.Resolve,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AttentionActionResult(AttentionActionOutcome.Ok, null));

        IActionResult result = await controller.ExecuteActionAsync(
            "maintenance:1",
            AttentionActionKind.Resolve,
            CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.Equal("Ok", json.RootElement.GetProperty("outcome").GetString());
        service.VerifyAll();
        broadcaster.VerifyNoOtherCalls();
    }

    private static (
        AttentionController Controller,
        Mock<IAttentionService> Service,
        Mock<IAttentionBroadcaster> Broadcaster) CreateController(bool harvestAction)
    {
        var service = new Mock<IAttentionService>(MockBehavior.Strict);
        var broadcaster = new Mock<IAttentionBroadcaster>(MockBehavior.Strict);
        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Strict);
        gate.Setup(value => value.IsEnabled(OperatorFeature.Attention)).Returns(true);
        if (harvestAction)
        {
            gate.Setup(value => value.IsEnabled(OperatorFeature.PrintedPartsInventory)).Returns(true);
        }

        var controller = new AttentionController(
            service.Object,
            broadcaster.Object,
            gate.Object,
            NullLogger<AttentionController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("sub", Guid.NewGuid().ToString("D")),
                    ], "test")),
                },
            },
        };
        return (controller, service, broadcaster);
    }
}
