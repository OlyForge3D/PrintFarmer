using System.Net;
using System.Text;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class MoonrakerVerifiedSafetyTests
{
    [Fact]
    public async Task DiscoverVerifiedSafetyAsync_AuthoritativeResponses_ReportsBackendFacts()
    {
        using var handler = new InlineHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            string json = path.EndsWith("/list", StringComparison.Ordinal)
                ? """
                  {"result":{"objects":["toolhead","gcode_move","gcode_macro LOAD_FILAMENT","gcode_macro M600"]}}
                  """
                : """
                  {"result":{"status":{"toolhead":{"axis_minimum":[0,0,0,0],"axis_maximum":[250,210,220,0]},"gcode_move":{"homing_origin":[0,0,0,0]}}}}
                  """;
            return JsonResponse(json);
        });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());

        PrinterVerifiedSafetyDto result =
            await ((ISupportsVerifiedSafetyDiscovery)client)
                .DiscoverVerifiedSafetyAsync(
                    "http://printer.local/",
                    null,
                    "7",
                    CancellationToken.None);

        Assert.Equal(VerifiedSafetyDiscoveryState.Partial, result.Discovery.State);
        Assert.Equal("7", result.Discovery.SourceRevision);
        Assert.Equal(
            VerifiedSafetySupport.Supported,
            result.Operations.AbsoluteMovement.Support);
        Assert.Equal(
            VerifiedSafetySupport.Unsupported,
            result.Operations.FirmwareZOffsetSave.Support);
        Assert.Equal(
            VerifiedSafetySupport.Supported,
            result.Operations.FilamentLoad.Support);
        Assert.Equal(
            VerifiedSafetySupport.Unsupported,
            result.Operations.FilamentUnload.Support);
        Assert.Equal(
            VerifiedSafetySupport.Supported,
            result.Operations.FilamentChange.Support);
        Assert.Equal(
            VerifiedSafetyFactState.Unknown,
            result.Extrusion.MinimumSafeMeasuredHotendTemperatureC.State);
        Assert.Equal(
            VerifiedSafetyFactState.Verified,
            result.Positioning.CoordinateOriginMm.State);
        Assert.Equal(
            VerifiedSafetyFactState.Verified,
            result.Positioning.TravelEnvelopeMm.State);
        Assert.Equal(
            VerifiedSafetyFactState.Unknown,
            result.Positioning.MinimumClearanceZMm.State);
    }

    [Fact]
    public async Task DiscoverVerifiedSafetyAsync_MalformedObjectList_ReturnsUnavailableUnknowns()
    {
        using var handler = new InlineHandler(_ => JsonResponse(
            """{"result":{"objects":"not-an-array"}}"""));
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());

        PrinterVerifiedSafetyDto result =
            await ((ISupportsVerifiedSafetyDiscovery)client)
                .DiscoverVerifiedSafetyAsync(
                    "http://printer.local/",
                    null,
                    "1",
                    CancellationToken.None);

        Assert.Equal(
            VerifiedSafetyDiscoveryState.Unavailable,
            result.Discovery.State);
        Assert.Equal(
            VerifiedSafetySupport.Unknown,
            result.Operations.FilamentLoad.Support);
    }

    [Fact]
    public async Task DiscoverVerifiedSafetyAsync_MissingMovementObject_ReturnsUnknownMovement()
    {
        using var handler = new InlineHandler(request =>
        {
            string json = request.RequestUri!.AbsolutePath.EndsWith(
                "/list",
                StringComparison.Ordinal)
                ? """{"result":{"objects":["toolhead"]}}"""
                : """
                  {"result":{"status":{"toolhead":{"axis_minimum":[0,0,0],"axis_maximum":[250,210,220]},"gcode_move":{"homing_origin":[0,0,0]}}}}
                  """;
            return JsonResponse(json);
        });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());

        PrinterVerifiedSafetyDto result =
            await ((ISupportsVerifiedSafetyDiscovery)client)
                .DiscoverVerifiedSafetyAsync(
                    "http://printer.local/",
                    null,
                    "1",
                    CancellationToken.None);

        Assert.Equal(
            VerifiedSafetySupport.Unknown,
            result.Operations.AbsoluteMovement.Support);
    }

    [Fact]
    public async Task MoveToAsync_ValidCoordinates_SendsModeAndMoveAsSeparateCommands()
    {
        string? body = null;
        using var handler = new InlineHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"result":"ok"}""");
        });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());

        bool result = await client.MoveToAsync(
            "http://printer.local/",
            x: 10,
            y: 20,
            z: 5,
            f: 1200,
            ct: CancellationToken.None);

        Assert.True(result);
        Assert.Contains("\"script\":\"G90\\nG0 X10 Y20 Z5 F1200\"", body);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class InlineHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
