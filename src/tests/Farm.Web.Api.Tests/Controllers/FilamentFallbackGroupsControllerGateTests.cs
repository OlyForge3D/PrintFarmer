using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Web.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Feature-gate coverage for <see cref="FilamentFallbackGroupsController"/> (issue #711, FIX E).
/// When the <see cref="OperatorFeature.MultiSlotFallback"/> operator feature is disabled every
/// endpoint must short-circuit with 404 before touching the service or emitting a SignalR
/// broadcast; when enabled the endpoints behave normally.
/// </summary>
public class FilamentFallbackGroupsControllerGateTests
{
    private readonly Mock<IFilamentFallbackGroupService> _service = new(MockBehavior.Strict);
    private readonly Mock<IOperatorFeatureGate> _gate = new(MockBehavior.Loose);
    private readonly Mock<IHubContext<PrinterHub>> _hub = new(MockBehavior.Strict);

    private FilamentFallbackGroupsController CreateController(bool enabled)
    {
        _gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(enabled);
        _gate.Setup(g => g.GetFlagName(OperatorFeature.MultiSlotFallback)).Returns("multiSlotFallbackEnabled");
        return new FilamentFallbackGroupsController(
            _service.Object,
            _gate.Object,
            _hub.Object,
            NullLogger<FilamentFallbackGroupsController>.Instance);
    }

    [Fact]
    public async Task List_WhenFeatureDisabled_Returns404AndSkipsService()
    {
        FilamentFallbackGroupsController controller = CreateController(enabled: false);

        ActionResult<IReadOnlyList<FilamentFallbackGroupDto>> result =
            await controller.ListAsync(Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
        _service.Verify(s => s.ListForPrinterAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_WhenFeatureDisabled_Returns404AndNeverBroadcasts()
    {
        FilamentFallbackGroupsController controller = CreateController(enabled: false);
        CreateFilamentFallbackGroupRequest request = new("PLA Chain", "PLA", null, [Guid.NewGuid()]);

        ActionResult<FilamentFallbackGroupDto> result =
            await controller.CreateAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
        _service.Verify(
            s => s.CreateAsync(It.IsAny<Guid>(), It.IsAny<CreateFilamentFallbackGroupRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Strict hub mock proves no SignalR broadcast was attempted while gated off.
    }

    [Fact]
    public async Task Available_WhenFeatureDisabled_Returns404()
    {
        FilamentFallbackGroupsController controller = CreateController(enabled: false);

        ActionResult<AvailableFallbackMember> result = await controller.GetAvailableFallbackAsync(
            Guid.NewGuid(), Guid.NewGuid(), "PLA", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
        _service.Verify(
            s => s.FindAvailableFallbackAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task List_WhenFeatureEnabled_ReturnsServiceResult()
    {
        Guid printerId = Guid.NewGuid();
        IReadOnlyList<FilamentFallbackGroupDto> groups = [];
        _service.Setup(s => s.ListForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(groups);
        FilamentFallbackGroupsController controller = CreateController(enabled: true);

        ActionResult<IReadOnlyList<FilamentFallbackGroupDto>> result =
            await controller.ListAsync(printerId, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(groups);
        _service.Verify(s => s.ListForPrinterAsync(printerId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
