using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.Discovery;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Controllers.Responses;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Unit-level coverage for the egress vetting wired into
/// <see cref="PrintersController.TestConnectionAsync"/> (issue #1422). Complements the HTTP-layer
/// role-gate and redirect tests in
/// <c>Farm.Web.Api.Tests.Security.PrintersControllerTestConnectionAuthorizationTests</c> by
/// verifying, at the unit level, exactly which URI the egress guard is asked to vet and that a
/// denial short-circuits before any backend client is ever obtained.
/// </summary>
public sealed class PrintersControllerTestConnectionEgressTests
{
    [Fact]
    public async Task DeniedDestination_ReturnsBadRequest_AndNeverTouchesBackendClientFactory()
    {
        var backendClientFactory = new Mock<IBackendClientFactory>(MockBehavior.Strict);
        var egressGuard = new Mock<IEgressGuard>();
        egressGuard
            .Setup(g => g.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EgressCheckResult.Deny("Destination resolves to a loopback address"));

        PrintersController controller = CreateController(backendClientFactory.Object, egressGuard.Object);

        var request = new TestConnectionRequest("http://127.0.0.1:7125", PrinterBackend.Moonraker);

        var result = await controller.TestConnectionAsync(request, CancellationToken.None);

        var badRequest = result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>().Subject;
        var body = badRequest.Value.Should().BeOfType<TestConnectionResponse>().Subject;
        body.Success.Should().BeFalse();
        body.Message.Should().Be("The requested server address is not allowed.");
        body.Message.Should().NotContain("loopback", "the denial reason must not be echoed to the caller");

        backendClientFactory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(PrinterBackend.SDCP)]
    [InlineData(PrinterBackend.FlashForge)]
    public async Task BackendPortOverride_ReVetsRewrittenUri_NotOriginal(PrinterBackend backend)
    {
        var connectionTestClient = new Mock<IBackendClient>().As<ISupportsConnectionTest>();
        connectionTestClient
            .Setup(c => c.TestConnectionAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var backendClientFactory = new Mock<IBackendClientFactory>();
        backendClientFactory.Setup(f => f.GetClient(backend)).Returns((IBackendClient)connectionTestClient.Object);

        System.Collections.Generic.List<string> vettedUrls = [];
        var egressGuard = new Mock<IEgressGuard>();
        egressGuard
            .Setup(g => g.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((url, _) => vettedUrls.Add(url))
            .ReturnsAsync((string url, CancellationToken _) => EgressCheckResult.Allow(new Uri(url)));

        PrintersController controller = CreateController(backendClientFactory.Object, egressGuard.Object);

        var request = new TestConnectionRequest(
            "http://192.168.1.50:8080",
            backend,
            BackendPort: 8899);

        var result = await controller.TestConnectionAsync(request, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<TestConnectionResponse>().Subject;
        body.Success.Should().BeTrue();

        vettedUrls.Should().HaveCount(2, "the original URL is vetted at entry and the port-rewritten URI is re-vetted before dialling");
        vettedUrls[0].Should().Contain(":8080", "the first check vets the caller-supplied URL as submitted");
        vettedUrls[1].Should().Contain(":8899", "the second check must vet the URI actually rewritten by the backendPort override, not the original");

        connectionTestClient.Verify(
            c => c.TestConnectionAsync(It.Is<Uri>(u => u.Port == 8899), It.IsAny<CancellationToken>()),
            Times.Once,
            "the backend client must be dialled at the rewritten (vetted) port");
    }

    [Theory]
    [InlineData(PrinterBackend.SDCP)]
    [InlineData(PrinterBackend.FlashForge)]
    public async Task BackendPortOverride_DeniedOnRewrittenUri_NeverCallsBackendClient(PrinterBackend backend)
    {
        var backendClientFactory = new Mock<IBackendClientFactory>(MockBehavior.Strict);
        var egressGuard = new Mock<IEgressGuard>();
        egressGuard
            .Setup(g => g.CheckAsync(It.Is<string>(url => url.Contains(":8080")), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) => EgressCheckResult.Allow(new Uri(url)));
        egressGuard
            .Setup(g => g.CheckAsync(It.Is<string>(url => url.Contains(":22")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EgressCheckResult.Deny("Destination resolves to a loopback address"));

        PrintersController controller = CreateController(backendClientFactory.Object, egressGuard.Object);

        var request = new TestConnectionRequest(
            "http://192.168.1.50:8080",
            backend,
            BackendPort: 22);

        var result = await controller.TestConnectionAsync(request, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<TestConnectionResponse>().Subject;
        body.Success.Should().BeFalse();
        body.Message.Should().Be("The requested server address is not allowed.");

        backendClientFactory.VerifyNoOtherCalls();
    }

    private static PrintersController CreateController(
        IBackendClientFactory backendClientFactory,
        IEgressGuard egressGuard)
    {
        // TestBackendConnectionAsync unconditionally creates the "VettedEgress" client before
        // dispatching by backend, even for SDCP/FlashForge (which don't use it), so the factory
        // mock must return a real HttpClient rather than the default null.
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());

        var controller = new PrintersController(
            logger: Mock.Of<ILogger<PrintersController>>(),
            printersService: Mock.Of<IPrintersService>(),
            catalogService: Mock.Of<Farm.Web.Api.Services.Catalog.ICatalogService>(),
            validator: Mock.Of<IValidator<CreatePrinterFromDiscoveryDto>>(),
            discoveryProxyService: Mock.Of<IDiscoveryProxyService>(),
            discoverySessions: Mock.Of<IDiscoverySessionRegistry>(),
            printerBackendCapabilitiesService: Mock.Of<IPrinterBackendCapabilitiesService>(),
            backendClientFactory: backendClientFactory,
            httpClientFactory: httpClientFactory.Object,
            egressGuard: egressGuard,
            obicoServerAssignment: Mock.Of<Farm.Infrastructure.Services.FailureDetection.IObicoServerAssignmentService>(),
            settingsService: Mock.Of<Farm.Infrastructure.Settings.ISettingsService>(),
            printerSessionTimelineService: Mock.Of<IPrinterSessionTimelineService>(),
            telemetryService: Mock.Of<IPrintFarmerTelemetryService>(),
            bedTypeService: Mock.Of<Farm.Infrastructure.Services.BedTypes.IBedTypeService>());
        return controller;
    }
}
