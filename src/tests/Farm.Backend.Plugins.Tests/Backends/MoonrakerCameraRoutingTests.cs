using System.Net;
using System.Text;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class MoonrakerCameraRoutingTests
{
    [Theory]
    [InlineData("http://printer.local:7125", 80, "http://printer.local")]
    [InlineData("http://printer.local:4408", 4409, "http://printer.local:4409")]
    [InlineData("https://printer.local:7125", 443, "https://printer.local")]
    [InlineData("http://[::1]:7125", 80, "http://[::1]")]
    public async Task DetectConfiguredCameraUrlsAsync_SplitPorts_QueriesApiAndResolvesFrontend(
        string backendUrl, int frontendPort, string frontendUrl)
    {
        using var handler = new CameraHandler(testEndpointAvailable: true);
        using var http = new HttpClient(handler);
        ISupportsConfiguredCameraDetection client = CreateClient(http);
        var credential = new PrinterCredential { ApiKey = "test-camera-key" };

        (string? stream, string? snapshot) = await client.DetectConfiguredCameraUrlsAsync(
            backendUrl, frontendPort, credential, CancellationToken.None);

        Assert.Equal($"{frontendUrl}/webcam/?action=stream", stream);
        Assert.Equal($"{frontendUrl}/webcam/?action=snapshot", snapshot);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(new Uri(backendUrl).Authority, request.Url.Authority);
            Assert.Equal("test-camera-key", request.ApiKey);
        });
        Assert.Equal("/server/webcams/list", handler.Requests[0].Url.AbsolutePath);
        Assert.Equal("/server/webcams/test", handler.Requests[1].Url.AbsolutePath);
    }

    [Fact]
    public async Task DetectConfiguredCameraUrlsAsync_TestEndpointUnavailable_NormalizesListingAgainstFrontend()
    {
        using var handler = new CameraHandler(testEndpointAvailable: false);
        using var http = new HttpClient(handler);
        ISupportsConfiguredCameraDetection client = CreateClient(http);

        (string? stream, string? snapshot) = await client.DetectConfiguredCameraUrlsAsync(
            "http://printer.local:7125", 8080);

        Assert.Equal("http://printer.local:8080/webcam/?action=stream", stream);
        Assert.Equal("http://printer.local:8080/webcam/?action=snapshot", snapshot);
    }

    [Fact]
    public async Task GetCameraSnapshotUrlAsync_ThroughCapability_PreservesFrontendPort()
    {
        using var http = new HttpClient();
        ISupportsCamera client = CreateClient(http);

        string? snapshot = await client.GetCameraSnapshotUrlAsync("http://printer.local:7125", 8080);

        Assert.Equal("http://printer.local:8080/webcam/?action=snapshot", snapshot);
    }

    [Theory]
    [InlineData("Snapmaker", "Snapmaker U1", true)]
    [InlineData("Voron", "V2.4", false)]
    public void CameraProfile_ModelIdentity_SelectsOnlyKnownTriggeredTransport(
        string manufacturer, string model, bool isTriggered)
    {
        using var http = new HttpClient();
        MoonrakerClient client = CreateClient(http);
        var printer = new Printer
        {
            Name = "test",
            ServerUrl = "http://printer.local",
            BackendPort = 7125,
            FrontendPort = 80,
            Manufacturer = new Manufacturer { Name = manufacturer },
            Model = new PrinterModel { Name = model },
        };

        (string? stream, string? snapshot) = client.GetDefaultCameraUrls(printer);

        Assert.Null(stream);
        Assert.Equal(isTriggered ? "http://printer.local:7125/server/files/camera/monitor.jpg" : null, snapshot);
        Assert.Equal(isTriggered, client.ShouldTriggerSnapshot(printer, snapshot));
        Assert.False(client.ShouldTriggerSnapshot(printer, "http://camera.local/snapshot.jpg"));
    }

    private static MoonrakerClient CreateClient(HttpClient http) =>
        new(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());

    private sealed class CameraHandler(bool testEndpointAvailable) : HttpMessageHandler
    {
        public List<(Uri Url, string? ApiKey)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? keys) ? keys.Single() : null));
            if (request.Method == HttpMethod.Post && !testEndpointAvailable)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            string payload = request.Method == HttpMethod.Get
                ? """{"result":{"webcams":[{"name":"camera","stream_url":"/webcam/?action=stream","snapshot_url":"/webcam/?action=snapshot"}]}}"""
                : """{"result":{"stream_url":"/webcam/?action=stream","snapshot_url":"/webcam/?action=snapshot"}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }
}
