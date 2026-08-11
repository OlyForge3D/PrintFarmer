using System;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Controllers.Responses;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Unit tests proving that <see cref="PrintersController.TestConnectionAsync"/> is wired through
/// the egress guard so a denied destination short-circuits before any backend call is made, and
/// an allowed/pinned destination reuses the exact IP the guard vetted for the real outbound
/// connection rather than letting it be re-resolved independently at connect time.
/// See GitHub issue OlyForge3D/PrintFarmer#1430.
/// </summary>
public class PrintersControllerTestConnectionTests
{
    [Fact]
    public async Task TestConnectionAsync_WhenEgressGuardDenies_ShortCircuitsWithZeroBackendCalls()
    {
        Mock<IHttpClientFactory> httpClientFactory = new(MockBehavior.Strict);
        Mock<IEgressGuard> egressGuard = new(MockBehavior.Strict);
        egressGuard
            .Setup(guard => guard.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) =>
                EgressCheckResult.Deny("Destination resolves to a loopback, link-local, or multicast address", new Uri(url)));

        PrintersController controller = CreateController(httpClientFactory.Object, egressGuard.Object);

        var request = new TestConnectionRequest(
            ServerUrl: "http://169.254.169.254/",
            Backend: PrinterBackend.Moonraker);

        ActionResult<TestConnectionResponse> result = await controller.TestConnectionAsync(request, CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        TestConnectionResponse response = Assert.IsType<TestConnectionResponse>(badRequest.Value);
        response.Success.Should().BeFalse();
        response.Message.Should().Be("The requested server address is not allowed.");
        response.Message.Should().NotContain("loopback", "the specific deny reason must not be echoed to the caller");

        // Strict mock with no CreateClient setup: any attempt to build an HttpClient (i.e. any
        // attempt to actually connect to the backend) would throw, proving the guard short-circuited.
        httpClientFactory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TestConnectionAsync_WhenEgressGuardResolvesAnAddress_ConnectionTargetsThePinnedIpNotTheHostname()
    {
        HttpRequestMessage? capturedRequest = null;
        Mock<IHttpClientFactory> httpClientFactory = new(MockBehavior.Strict);
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new RecordingHandler(request =>
            {
                capturedRequest = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"result\":{\"state_message\":\"Ready\",\"klipper_path\":\"/klipper\",\"hostname\":\"printer-1\"}}",
                        Encoding.UTF8,
                        "application/json")
                };
            })));

        Mock<IEgressGuard> egressGuard = new(MockBehavior.Strict);
        egressGuard
            .Setup(guard => guard.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) =>
                EgressCheckResult.Allow(new Uri(url), IPAddress.Parse("203.0.113.5")));

        PrintersController controller = CreateController(httpClientFactory.Object, egressGuard.Object);

        var request = new TestConnectionRequest(
            ServerUrl: "http://printer.local:7125/",
            Backend: PrinterBackend.Moonraker);

        ActionResult<TestConnectionResponse> result = await controller.TestConnectionAsync(request, CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        TestConnectionResponse response = Assert.IsType<TestConnectionResponse>(okResult.Value);
        response.Success.Should().BeTrue();

        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri.Should().NotBeNull();
        capturedRequest.RequestUri!.Host.Should().Be("203.0.113.5", "the connection must target the vetted IP, not re-resolve the hostname");
        capturedRequest.Headers.Host.Should().Be("printer.local:7125", "the original hostname must be preserved via the Host header for virtual-hosting/SNI");
    }

    private static PrintersController CreateController(IHttpClientFactory httpClientFactory, IEgressGuard egressGuard)
    {
        var controller = new PrintersController(
            logger: Mock.Of<ILogger<PrintersController>>(),
            printersService: Mock.Of<IPrintersService>(),
            catalogService: Mock.Of<Farm.Web.Api.Services.Catalog.ICatalogService>(),
            validator: Mock.Of<IValidator<CreatePrinterFromDiscoveryDto>>(),
            discoveryProxyService: Mock.Of<Farm.Infrastructure.Services.Discovery.IDiscoveryProxyService>(),
            discoverySessions: Mock.Of<Farm.Infrastructure.Services.Discovery.IDiscoverySessionRegistry>(),
            printerBackendCapabilitiesService: Mock.Of<IPrinterBackendCapabilitiesService>(),
            backendClientFactory: Mock.Of<IBackendClientFactory>(),
            httpClientFactory: httpClientFactory,
            egressGuard: egressGuard,
            obicoServerAssignment: Mock.Of<Farm.Infrastructure.Services.FailureDetection.IObicoServerAssignmentService>(),
            settingsService: Mock.Of<Farm.Infrastructure.Settings.ISettingsService>(),
            printerSessionTimelineService: Mock.Of<IPrinterSessionTimelineService>(),
            telemetryService: Mock.Of<Farm.Infrastructure.Telemetry.IPrintFarmerTelemetryService>(),
            bedTypeService: Mock.Of<Farm.Infrastructure.Services.BedTypes.IBedTypeService>());

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

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }
}
