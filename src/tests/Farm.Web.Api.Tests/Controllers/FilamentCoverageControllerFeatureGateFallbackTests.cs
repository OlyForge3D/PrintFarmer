using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Proves that <see cref="FilamentCoverageController"/> — a representative migrated,
/// gated controller — degrades gracefully (no HTTP 500) when the operator feature gate's
/// persisted-settings acquisition fails, because it uses the general fallback
/// <see cref="IOperatorFeatureGate.IsEnabledAsync"/> path (issue #755 Hicks blocker 1).
///
/// These are controller-level unit tests over the <b>real</b>
/// <see cref="OperatorFeatureGate"/> backed by a throwing repository, so the controller +
/// gate integration is exercised without spinning up a web host. On the pre-fix code the
/// gate propagated the repository exception out of the action (an unhandled 500); on the
/// fixed code it degrades to the documented FilamentCoverage default (enabled) and the
/// action runs to a normal result.
/// </summary>
public class FilamentCoverageControllerFeatureGateFallbackTests
{
    private static OperatorFeatureGate GateOverFailingRepository()
    {
        Mock<IAppSettingsRepository> repo = new();
        repo.Setup(r => r.GetReadOnlyAsync(OperatorFeatureSettings.SectionName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cold start / DB unavailable"));
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        return new OperatorFeatureGate(repo.Object, config, NullLogger<OperatorFeatureGate>.Instance);
    }

    [Fact]
    public async Task GetForPrinterAsync_WhenFeatureGateRepositoryDown_ProceedsToCoverageAndReturnsNotFound()
    {
        Guid printerId = Guid.NewGuid();
        Mock<IFilamentCoverageService> coverage = new();
        coverage
            .Setup(s => s.GetForPrinterAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterFilamentCoverageDto?)null);
        var controller = new FilamentCoverageController(
            coverage.Object,
            GateOverFailingRepository(),
            NullLogger<FilamentCoverageController>.Instance);

        ActionResult<PrinterFilamentCoverageDto> result =
            await controller.GetForPrinterAsync(printerId, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>(
            "the fallback gate returns the FilamentCoverage default (enabled) so the action runs to a normal 404 rather than propagating a DB exception as a 500");
        coverage.Verify(
            s => s.GetForPrinterAsync(printerId, It.IsAny<CancellationToken>()),
            Times.Once,
            "the gated action must proceed to the coverage service on the fallback path");
    }

    [Fact]
    public async Task GetForFleetAsync_WhenFeatureGateRepositoryDown_ReturnsOkWithoutThrowing()
    {
        var fleet = new FleetFilamentCoverageDto(Array.Empty<PrinterFilamentCoverageDto>(), DateTime.UtcNow);
        Mock<IFilamentCoverageService> coverage = new();
        coverage
            .Setup(s => s.GetForFleetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(fleet);
        var controller = new FilamentCoverageController(
            coverage.Object,
            GateOverFailingRepository(),
            NullLogger<FilamentCoverageController>.Instance);

        ActionResult<FleetFilamentCoverageDto> result =
            await controller.GetForFleetAsync(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>(
            "a feature-gate repository outage must degrade to the enabled default rather than surface as a 500");
        ((OkObjectResult)result.Result!).Value.Should().BeSameAs(fleet);
    }
}
