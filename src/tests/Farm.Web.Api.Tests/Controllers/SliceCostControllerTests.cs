using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.Cost;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Responses;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="SliceCostController"/>.
/// </summary>
public sealed class SliceCostControllerTests
{
    private readonly Mock<IFilamentCostProvider> _costProviderMock = new();
    private readonly SliceCostController _controller;

    public SliceCostControllerTests()
    {
        _controller = new SliceCostController(_costProviderMock.Object);
    }

    // =========================================================================
    // spoolId path
    // =========================================================================

    [Fact]
    [Trait("Category", "SliceCost")]
    public async Task GetCostPerGram_SpoolId_ReturnsCostAndSourceSpool()
    {
        _costProviderMock
            .Setup(p => p.GetSpoolCostPerGramAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.025m);

        IActionResult result = await _controller.GetCostPerGramAsync(spoolId: 42, filamentId: null, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<SliceCostResponse>().Subject;
        response.CostPerGram.Should().Be(0.025m);
        response.Source.Should().Be("spool");
        response.Currency.Should().Be("USD");
    }

    // =========================================================================
    // filamentId path
    // =========================================================================

    [Fact]
    [Trait("Category", "SliceCost")]
    public async Task GetCostPerGram_FilamentId_ReturnsCostAndSourceFilament()
    {
        _costProviderMock
            .Setup(p => p.GetFilamentCostPerGramAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.018m);

        IActionResult result = await _controller.GetCostPerGramAsync(spoolId: null, filamentId: 7, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<SliceCostResponse>().Subject;
        response.CostPerGram.Should().Be(0.018m);
        response.Source.Should().Be("filament");
        response.Currency.Should().Be("USD");
    }

    // =========================================================================
    // Provider returns null (Spoolman unavailable)
    // =========================================================================

    [Fact]
    [Trait("Category", "SliceCost")]
    public async Task GetCostPerGram_ProviderReturnsNull_Returns200WithNullCostAndNullSource()
    {
        _costProviderMock
            .Setup(p => p.GetSpoolCostPerGramAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal?)null);

        IActionResult result = await _controller.GetCostPerGramAsync(spoolId: 99, filamentId: null, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<SliceCostResponse>().Subject;
        response.CostPerGram.Should().BeNull();
        response.Source.Should().BeNull();
    }

    // =========================================================================
    // Neither param → 400
    // =========================================================================

    [Fact]
    [Trait("Category", "SliceCost")]
    public async Task GetCostPerGram_NeitherParam_Returns400()
    {
        IActionResult result = await _controller.GetCostPerGramAsync(spoolId: null, filamentId: null, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }
}
