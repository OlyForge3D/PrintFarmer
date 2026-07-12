using System.Security.Claims;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Repositories.PartsInventory;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Web.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class PartsInventoryControllerTests
{
    [Fact]
    public async Task GetAllAsync_FeatureDisabled_Returns404BeforeRepositoryRead()
    {
        var parts = new Mock<IPartInventoryRepository>(MockBehavior.Strict);
        Mock<IOperatorFeatureGate> gate = CreateGate(enabled: false);
        PartsInventoryController controller = CreatePartsController(parts.Object, gate.Object);

        ActionResult<IReadOnlyList<PartInventoryResponse>> result =
            await controller.GetAllAsync(ct: CancellationToken.None);

        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        ProblemDetails problem = Assert.IsType<ProblemDetails>(notFound.Value);
        Assert.Equal("featureDisabled", problem.Extensions["code"]);
        parts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AdjustAsync_AuthenticatedActor_UsesNameIdentifierClaim()
    {
        string actorId = Guid.NewGuid().ToString("D");
        var service = new Mock<IPartInventoryService>(MockBehavior.Strict);
        service
            .Setup(value => value.AdjustAsync(
                "SKU-1",
                It.Is<AdjustCommand>(command => command.UserId == actorId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdjustResult(
                PartInventoryOutcome.Ok,
                new PartAdjustmentResponse(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "SKU-1",
                    null,
                    null,
                    1,
                    PartAdjustmentReason.Manual,
                    null,
                    null,
                    null,
                    actorId,
                    DateTime.UtcNow),
                1,
                null));
        Mock<IOperatorFeatureGate> gate = CreateGate(enabled: true);
        PartsInventoryController controller = CreatePartsController(
            new Mock<IPartInventoryRepository>().Object,
            gate.Object,
            service.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, actorId),
                    new Claim(ClaimTypes.Name, "spoofable-display-name"),
                ], "test")),
            },
        };

        ActionResult<PartAdjustmentResponse> result = await controller.AdjustAsync(
            "SKU-1",
            new AdjustPartInventoryRequest(1, PartAdjustmentReason.Manual),
            CancellationToken.None);

        _ = Assert.IsType<OkObjectResult>(result.Result);
        service.VerifyAll();
    }

    [Fact]
    public async Task ResolveByBarcodeAsync_FeatureDisabled_DoesNotReadOrLog()
    {
        var bins = new Mock<IBinRepository>(MockBehavior.Strict);
        var logs = new Mock<IBarcodeScanLogService>(MockBehavior.Strict);
        Mock<IOperatorFeatureGate> gate = CreateGate(enabled: false);
        var controller = new BinsController(
            NullLogger<BinsController>.Instance,
            bins.Object,
            logs.Object,
            gate.Object);

        ActionResult<BinResponse> result = await controller.ResolveByBarcodeAsync("BIN-1", CancellationToken.None);

        _ = Assert.IsType<NotFoundObjectResult>(result.Result);
        bins.VerifyNoOtherCalls();
        logs.VerifyNoOtherCalls();
    }

    private static Mock<IOperatorFeatureGate> CreateGate(bool enabled)
    {
        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Strict);
        gate.Setup(value => value.IsEnabled(OperatorFeature.PrintedPartsInventory)).Returns(enabled);
        if (!enabled)
        {
            gate.Setup(value => value.GetFlagName(OperatorFeature.PrintedPartsInventory))
                .Returns("printedPartsInventoryEnabled");
        }

        return gate;
    }

    private static PartsInventoryController CreatePartsController(
        IPartInventoryRepository parts,
        IOperatorFeatureGate gate,
        IPartInventoryService? service = null)
        => new(
            NullLogger<PartsInventoryController>.Instance,
            parts,
            new Mock<IBinRepository>().Object,
            new Mock<IPartInventoryAdjustmentRepository>().Object,
            new Mock<IPartOutputMappingRepository>().Object,
            service ?? new Mock<IPartInventoryService>().Object,
            new Mock<IReorderEvaluationService>().Object,
            new Mock<IBarcodeScanLogService>().Object,
            gate);
}
