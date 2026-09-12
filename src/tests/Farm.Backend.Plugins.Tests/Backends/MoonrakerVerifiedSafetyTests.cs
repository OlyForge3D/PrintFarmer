using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class MoonrakerVerifiedSafetyTests
{
    [Fact]
    public async Task GetCompositeStatusAsync_GcodePositionPresent_PrefersGcodeCoordinates()
    {
        using var handler = new InlineHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/printer/info" => JsonResponse("""{"result":{"state":"ready"}}"""),
            "/printer/objects/query" when request.RequestUri.Query.Contains(
                "print_stats",
                StringComparison.Ordinal) =>
                JsonResponse("""{"result":{"status":{"print_stats":{"state":"standby"}}}}"""),
            "/printer/objects/query" when request.RequestUri.Query.Contains(
                "gcode_move",
                StringComparison.Ordinal) =>
                JsonResponse(
                    """
                    {
                      "result":{
                        "status":{
                          "toolhead":{"position":[100,200,300,0]},
                          "gcode_move":{"gcode_position":[10,20,30,0]}
                        }
                      }
                    }
                    """),
            "/printer/objects/query" => JsonResponse("""{"result":{"status":{}}}"""),
            "/server/webcams/list" => JsonResponse("""{"result":{"webcams":[]}}"""),
            _ => JsonResponse("""{"result":{}}"""),
        });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());

        PrinterCompositeStatus status = await client.GetCompositeStatusAsync(
            "http://printer.local/",
            CancellationToken.None);

        Assert.Equal(10, status.X);
        Assert.Equal(20, status.Y);
        Assert.Equal(30, status.Z);
    }

    [Fact]
    public void HandlePositionUpdate_GcodePositionPresent_OverridesToolheadPosition()
    {
        var state = new PrinterState();
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "toolhead":{"position":[100,200,300,0]},
              "gcode_move":{"gcode_position":[10,20,30,0]}
            }
            """);

        MoonrakerSubscriptionService.HandlePositionUpdate(
            state,
            document.RootElement);

        Assert.Equal(10, state.X);
        Assert.Equal(20, state.Y);
        Assert.Equal(30, state.Z);
    }

    [Fact]
    public void HandlePositionUpdate_GcodePositionAbsent_RetainsToolheadPosition()
    {
        var state = new PrinterState();
        using JsonDocument document = JsonDocument.Parse(
            """{"toolhead":{"position":[10,20,30,0]},"gcode_move":{}}""");

        MoonrakerSubscriptionService.HandlePositionUpdate(
            state,
            document.RootElement);

        Assert.Equal(10, state.X);
        Assert.Equal(20, state.Y);
        Assert.Equal(30, state.Z);
    }

    [Fact]
    public void HandlePositionUpdate_ToolheadDeltaAfterGcodePosition_RetainsGcodeCoordinates()
    {
        var state = new PrinterState();
        using JsonDocument gcodeDocument = JsonDocument.Parse(
            """{"gcode_move":{"gcode_position":[10,20,30,0]}}""");
        using JsonDocument toolheadDocument = JsonDocument.Parse(
            """{"toolhead":{"position":[100,200,300,0]}}""");

        MoonrakerSubscriptionService.HandlePositionUpdate(
            state,
            gcodeDocument.RootElement);
        MoonrakerSubscriptionService.HandlePositionUpdate(
            state,
            toolheadDocument.RootElement);

        Assert.Equal(10, state.X);
        Assert.Equal(20, state.Y);
        Assert.Equal(30, state.Z);
    }

    [Fact]
    public async Task EmergencyStopAsync_BypassesPendingGcodeRequest()
    {
        using var handler = new EmergencyHandler();
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());
        using var cancellation = new CancellationTokenSource();
        Task<bool> motion = client.SendGcodeAsync("http://fixture.invalid/", "G28", cancellation.Token);
        await handler.MotionSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(await client.EmergencyStopAsync("http://fixture.invalid/", PrinterCredential.FromApiKey("fixture-key")).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(motion.IsCompleted);
        Assert.Equal(1, handler.EmergencyRequests);
        await cancellation.CancelAsync();
        Assert.False(await motion);
    }

    private sealed class EmergencyHandler : HttpMessageHandler
    {
        public TaskCompletionSource MotionSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int EmergencyRequests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            if (request.RequestUri!.AbsolutePath == "/printer/emergency_stop")
            {
                Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
                Assert.Empty(request.RequestUri.Query);
                EmergencyRequests++;
                return JsonResponse("""{"result":"ok"}""");
            }

            Assert.Equal("/printer/gcode/script", request.RequestUri.AbsolutePath);
            MotionSent.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The simulated G-code request must be cancelled.");
        }
    }

    [Theory]
    [InlineData("\"xyz\"", true)]
    [InlineData("\"\"", true)]
    [InlineData("null", false)]
    [InlineData("42", false)]
    [InlineData("{}", false)]
    [InlineData("missing", false)]
    [InlineData("failure", false)]
    public async Task StatusPoll_HomedAxes_ExposesRealObservedFactWithoutInventedFreshness(string axesJson, bool observed)
    {
        var requests = new List<Uri>();
        using var handler = new InlineHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            requests.Add(request.RequestUri!);
            if (axesJson == "failure" && request.RequestUri!.Query.Contains("toolhead=position,homed_axes", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }

            string axesField = axesJson is "missing" or "failure" ? string.Empty : $",\"homed_axes\":{axesJson}";
            return JsonResponse("""
                {"result":{"status":{"webhooks":{"state":"ready"},"print_stats":{"state":"standby"},"toolhead":{"position":[1,2,3]AXES_FIELD},"gcode_move":{"gcode_position":[11,22,33,0],"homing_origin":[10,20,30,0]}}}}
                """.Replace("AXES_FIELD", axesField, StringComparison.Ordinal));
        });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());
        var breakers = new Mock<ICircuitBreakerService>();
        breakers.Setup(service => service.GetCircuitBreaker(It.IsAny<string>(), null, null, null)).Returns(new CircuitBreaker());
        var statusClient = new MoonrakerStatusClient(client, breakers.Object,
            new ManagedSpoolProviderHelper(Mock.Of<ISpoolmanStatusCache>(), NullLogger<ManagedSpoolProviderHelper>.Instance),
            NullLogger<MoonrakerStatusClient>.Instance);
        DateTime before = DateTime.UtcNow;
        PrinterStatusDto status = await statusClient.GetPrinterStatusAsync(
            new Printer { Id = Guid.NewGuid(), Name = "Safety fixture", ServerUrl = "http://fixture.invalid", BackendPort = 7125 }, default);
        Assert.Contains(requests, uri => uri.Query.Contains("toolhead=position,homed_axes", StringComparison.Ordinal));
        Assert.Contains(requests, uri => uri.Query.Contains("toolhead=position,homed_axes&gcode_move=gcode_position", StringComparison.Ordinal));
        if (axesJson != "failure")
        {
            Assert.Equal(11, status.X);
            Assert.Equal(22, status.Y);
            Assert.Equal(33, status.Z);
        }

        SafetyAxesTelemetryFactDto fact = Assert.IsType<PrinterSafetyTelemetryDto>(status.SafetyTelemetry).HomedAxes;
        Assert.Equal(15, fact.StaleAfterSeconds);
        Assert.Equal("moonraker:toolhead.homed_axes", fact.Source);
        if (observed)
        {
            DateTime timestamp = Assert.IsType<DateTime>(fact.ObservedAtUtc);
            Assert.InRange(timestamp, before, DateTime.UtcNow);
            Assert.Equal(axesJson == "\"xyz\"" ? ["x", "y", "z"] : Array.Empty<string>(), fact.Value);
            PrinterStatusDto later = PrinterSafetyTelemetryNormalizer.Normalize(status, null, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(timestamp, later.SafetyTelemetry?.HomedAxes.ObservedAtUtc);
        }
        else
        {
            Assert.Null(fact.Value);
            Assert.Null(fact.ObservedAtUtc);
        }

        using JsonDocument wire = JsonDocument.Parse(JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.True(wire.RootElement.GetProperty("safetyTelemetry").GetProperty("homedAxes").TryGetProperty("staleAfterSeconds", out _));
    }

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
    public async Task DiscoverVerifiedSafetyAsync_HappyHareMmuObject_ReportsOnlyMmuControlsSupported()
    {
        using var handler = new InlineHandler(request =>
        {
            string json = request.RequestUri!.AbsolutePath.EndsWith(
                "/list",
                StringComparison.Ordinal)
                ? """{"result":{"objects":["mmu"]}}"""
                : """{"result":{"status":{}}}""";
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
            VerifiedSafetySupport.Unsupported,
            result.Operations.FilamentLoad.Support);
        Assert.Equal(
            VerifiedSafetySupport.Unsupported,
            result.Operations.FilamentUnload.Support);
        Assert.Equal(
            VerifiedSafetySupport.Unsupported,
            result.Operations.FilamentChange.Support);
        Assert.Equal(
            VerifiedSafetySupport.Supported,
            result.Operations.MmuChangeTool.Support);
        Assert.Equal(
            VerifiedSafetySupport.Supported,
            result.Operations.MmuLoad.Support);
        Assert.Equal(
            VerifiedSafetySupport.Supported,
            result.Operations.MmuEject.Support);
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
