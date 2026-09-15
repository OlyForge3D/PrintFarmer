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
            if (axesJson == "failure" && request.RequestUri!.Query.Contains("homed_axes", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }

            string axesField = axesJson is "missing" or "failure" ? string.Empty : $",\"homed_axes\":{axesJson}";
            return JsonResponse("""
                {"result":{"status":{"webhooks":{"state":"ready"},"print_stats":{"state":"standby"},"toolhead":{"position":[1,2,3]AXES_FIELD},"gcode_move":{"position":[21,42,63,0],"gcode_position":[11,22,33,0],"homing_origin":[10,20,30,0]}}}}
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
        Assert.Contains(requests, uri => uri.Query.Contains("toolhead=homed_axes&gcode_move=position,gcode_position", StringComparison.Ordinal));
        if (observed)
        {
            Assert.Equal(11, status.X);
            Assert.Equal(22, status.Y);
            Assert.Equal(33, status.Z);
        }

        SafetyAxesTelemetryFactDto fact = (status.SafetyTelemetry ?? PrinterSafetyTelemetryDto.Empty).HomedAxes;
        Assert.Equal(15, fact.StaleAfterSeconds);
        if (observed)
        {
            Assert.Equal("moonraker:toolhead.homed_axes", fact.Source);
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
        if (status.SafetyTelemetry is not null)
        {
            Assert.True(wire.RootElement.GetProperty("safetyTelemetry").GetProperty("homedAxes").TryGetProperty("staleAfterSeconds", out _));
        }
        else
        {
            Assert.False(status.IsOnline);
        }
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

    [Theory]
    [InlineData("jog", null)]
    [InlineData("move_to", null)]
    [InlineData("z_only", null)]
    [InlineData("configured_negative_z", null)]
    [InlineData("unhomed", "printer_axes_not_homed")]
    [InlineData("stale_homing", "printer_telemetry_stale")]
    [InlineData("stale_frame", "printer_telemetry_stale")]
    [InlineData("future_frame", "printer_telemetry_stale")]
    [InlineData("missing_frame", "printer_telemetry_missing")]
    [InlineData("missing_position", "printer_move_out_of_bounds")]
    [InlineData("nonfinite_position", "printer_move_out_of_bounds")]
    [InlineData("outside_current", "printer_move_out_of_bounds")]
    [InlineData("outside_target", "printer_move_out_of_bounds")]
    [InlineData("offset_target", "printer_move_out_of_bounds")]
    [InlineData("missing_geometry", "printer_safety_evidence_unknown")]
    [InlineData("strict_workflow", "printer_safety_evidence_unknown")]
    public async Task ValidateObservedManualMoveAsync_RealMoonrakerDiscovery_EnforcesManualPolicy(
        string scenario, string? expectedCode)
    {
        // Only the HTTP transport is replaced: discovery must retain its real Unknown clearance.
        using var handler = new InlineHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            string json = request.RequestUri!.AbsolutePath.EndsWith("/list", StringComparison.Ordinal)
                ? """{"result":{"objects":["toolhead","gcode_move"]}}"""
                : scenario == "missing_geometry"
                    ? """{"result":{"status":{}}}"""
                    : """{"result":{"status":{"toolhead":{"axis_minimum":[0,0,-2,0],"axis_maximum":[250,210,220,0]},"gcode_move":{"homing_origin":[0,0,0,0]}}}}""";
            return JsonResponse(json);
        });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());
        Guid printerId = Guid.NewGuid();
        var capabilities = new Mock<IPrinterBackendCapabilitiesService>(MockBehavior.Strict);
        capabilities.Setup(service => service.InvalidateVerifiedSafety(printerId));
        capabilities.Setup(service => service.GetByPrinterIdAsync(printerId, It.IsAny<CancellationToken>()))
            .Returns(async (Guid id, CancellationToken token) =>
            {
                PrinterVerifiedSafetyDto discovered = await ((ISupportsVerifiedSafetyDiscovery)client)
                    .DiscoverVerifiedSafetyAsync("http://printer.invalid/", null, "1", token);
                Assert.Equal(VerifiedSafetyFactState.Unknown, discovered.Positioning.MinimumClearanceZMm.State);
                Assert.Null(discovered.Positioning.MinimumClearanceZMm.Value);
                return new PrinterBackendCapabilitiesDto(id, "Moonraker", PrinterBackend.Moonraker)
                {
                    VerifiedSafety = discovered,
                };
            });
        DateTime now = DateTime.UtcNow;
        var facts = PrinterSafetyTelemetryDto.Empty with
        {
            HomedAxes = new(["x", "y", "z"], now, 15, "moonraker:toolhead.homed_axes"),
            CoordinateOriginOffsetMm = new(new(0, 0, 0), now, 15, "moonraker:gcode_move.position-gcode_position"),
        };
        var observed = new PrinterStatusDto(printerId, true, "Idle", X: 50, Y: 60, Z: 0, SafetyTelemetry: facts);
        observed = scenario switch
        {
            "unhomed" => observed with { SafetyTelemetry = facts with { HomedAxes = facts.HomedAxes with { Value = ["x", "y"] } } },
            "stale_homing" => observed with { SafetyTelemetry = facts with { HomedAxes = facts.HomedAxes with { ObservedAtUtc = now.AddMinutes(-1) } } },
            "stale_frame" => observed with { SafetyTelemetry = facts with { CoordinateOriginOffsetMm = facts.CoordinateOriginOffsetMm with { ObservedAtUtc = now.AddMinutes(-1) } } },
            "future_frame" => observed with { SafetyTelemetry = facts with { CoordinateOriginOffsetMm = facts.CoordinateOriginOffsetMm with { ObservedAtUtc = now.AddMinutes(1) } } },
            "missing_frame" => observed with { SafetyTelemetry = facts with { CoordinateOriginOffsetMm = facts.CoordinateOriginOffsetMm with { Value = null } } },
            "offset_target" => observed with { SafetyTelemetry = facts with { CoordinateOriginOffsetMm = facts.CoordinateOriginOffsetMm with { Value = new(200, 0, 0) } } },
            "missing_position" => observed with { Y = null },
            "nonfinite_position" => observed with { Y = double.NaN },
            "outside_current" => observed with { X = 251 },
            _ => observed,
        };
        PrinterSafetyMoveRequest target = scenario switch
        {
            "jog" => new(observed.X + 1, observed.Y, observed.Z),
            "z_only" => new(null, null, 1),
            "configured_negative_z" => new(null, null, -1),
            "outside_target" => new(251, null, null),
            _ => new(51, null, null),
        };
        var cache = new Mock<IPrinterStatusCacheReader>(MockBehavior.Strict);
        if (scenario == "strict_workflow")
        {
            cache.Setup(reader => reader.GetStatus(printerId)).Returns(observed);
        }
        var guard = new PrinterSafetyGuard(capabilities.Object, cache.Object, TimeProvider.System);

        PrinterSafetyValidationResult result = scenario == "strict_workflow"
            ? await guard.ValidateAsync(printerId, PrinterSafetyOperation.AbsoluteMovement, new(51, 60, 0), default)
            : await guard.ValidateObservedManualMoveAsync(printerId, target, observed, default);

        Assert.Equal(expectedCode is null, result.Success);
        Assert.Equal(expectedCode, result.Code);
        capabilities.Verify(service => service.InvalidateVerifiedSafety(printerId), Times.Once);
        if (scenario != "strict_workflow")
        {
            cache.VerifyNoOtherCalls();
        }
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
        using JsonDocument document = JsonDocument.Parse(body!);
        Assert.Equal(
            "SAVE_GCODE_STATE NAME=printfarmer_manual\nG90\nG0 X10 Y20 Z5 F1200\nRESTORE_GCODE_STATE NAME=printfarmer_manual MOVE=0",
            document.RootElement.GetProperty("script").GetString());
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
