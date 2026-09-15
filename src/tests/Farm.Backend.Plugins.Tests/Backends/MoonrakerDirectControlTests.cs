using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class MoonrakerDirectControlTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetMovementStatusAsync_AuthenticatedSnapshot_UsesEffectiveFrameNotHomingOrigin(bool hasMachinePosition)
    {
        using var handler = new SnapshotHandler(hasMachinePosition);
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());
        var printer = new Printer
        {
            Id = Guid.NewGuid(), Name = "Snapshot fixture", Backend = (int)PrinterBackend.Moonraker,
            ServerUrl = "http://snapshot-fixture.invalid", BackendPort = 7125,
            Credential = PrinterCredential.FromApiKey("fixture-key"),
        };
        DateTime before = DateTime.UtcNow;

        PrinterStatusDto status = await client.GetMovementStatusAsync(printer, default);

        Assert.Equal(1, handler.Requests);
        Assert.True(status.IsOnline);
        Assert.Equal("standby", status.State);
        Assert.Equal(1.25, status.X);
        Assert.Equal(2.5, status.Y);
        Assert.Equal(0.125, status.Z);
        PrinterSafetyTelemetryDto facts = Assert.IsType<PrinterSafetyTelemetryDto>(status.SafetyTelemetry);
        Assert.Equal(["x", "y", "z"], Assert.IsType<string[]>(facts.HomedAxes.Value));
        Assert.InRange(Assert.IsType<DateTime>(facts.HomedAxes.ObservedAtUtc), before, DateTime.UtcNow);
        Assert.Equal("moonraker:gcode_move.position-gcode_position", facts.CoordinateOriginOffsetMm.Source);
        if (hasMachinePosition)
        {
            Assert.Equal(new SafetyVector3Dto(10, 20, 30), facts.CoordinateOriginOffsetMm.Value);
            Assert.InRange(Assert.IsType<DateTime>(facts.CoordinateOriginOffsetMm.ObservedAtUtc), before, DateTime.UtcNow);
        }
        else
        {
            Assert.Null(facts.CoordinateOriginOffsetMm.Value);
        }
    }

    [Theory]
    [InlineData("home", "G28")]
    [InlineData("homexy", "G28 X Y")]
    [InlineData("homez", "G28 Z")]
    [InlineData("move", "SAVE_GCODE_STATE NAME=printfarmer_manual\nG91\nG0 X1.25 Y-2.5 Z0.125 F1234.5\nRESTORE_GCODE_STATE NAME=printfarmer_manual MOVE=0")]
    [InlineData("moveto", "SAVE_GCODE_STATE NAME=printfarmer_manual\nG90\nG0 X1.25 Y-2.5 Z0.125 F1234.5\nRESTORE_GCODE_STATE NAME=printfarmer_manual MOVE=0")]
    public async Task ManualControlAsync_ApiCredential_SendsSingleInvariantScript(string operation, string script)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            using var handler = new ControlHandler();
            using var http = new HttpClient(handler);
            ISupportsMovement client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());

            Assert.True(await ExecuteControlAsync(client, operation));

            Assert.Equal(1, handler.Requests);
            Assert.Equal(script, handler.Script);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("home", "http")]
    [InlineData("move", "http")]
    [InlineData("moveto", "http")]
    [InlineData("home", "exception")]
    [InlineData("move", "exception")]
    [InlineData("moveto", "exception")]
    public async Task ManualControlAsync_AmbiguousFailure_DoesNotRetry(string operation, string failure)
    {
        using var handler = new ControlHandler { Failure = failure };
        using var http = new HttpClient(handler);
        ISupportsMovement client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());

        if (failure == "exception")
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => ExecuteControlAsync(client, operation));
        }
        else
        {
            Assert.False(await ExecuteControlAsync(client, operation));
        }

        Assert.Equal(1, handler.Requests);
    }

    private static Task<bool> ExecuteControlAsync(ISupportsMovement client, string operation) =>
        operation switch
        {
            "home" => client.HomeAsync("http://direct-fixture.invalid/", PrinterCredential.FromApiKey("fixture-key")),
            "homexy" => client.HomeXYAsync("http://direct-fixture.invalid/", PrinterCredential.FromApiKey("fixture-key"), default),
            "homez" => client.HomeZAsync("http://direct-fixture.invalid/", PrinterCredential.FromApiKey("fixture-key"), default),
            "move" => client.MoveAsync("http://direct-fixture.invalid/", 1.25, -2.5, 0.125, 1234.5, PrinterCredential.FromApiKey("fixture-key")),
            _ => client.MoveToAsync("http://direct-fixture.invalid/", 1.25, -2.5, 0.125, 1234.5, PrinterCredential.FromApiKey("fixture-key")),
        };

    private sealed class ControlHandler : HttpMessageHandler
    {
        public string? Failure { get; init; }
        public int Requests { get; private set; }
        public string? Script { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/printer/gcode/script", request.RequestUri!.AbsolutePath);
            Assert.Empty(request.RequestUri.Query);
            Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Script = body.RootElement.GetProperty("script").GetString();
            if (Failure == "exception")
            {
                throw new HttpRequestException("Connection lost after send");
            }

            return new HttpResponseMessage(Failure == "http" ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            {
                Content = new StringContent("""{"result":"ok"}""", Encoding.UTF8, "application/json"),
            };
        }

    }

    private sealed class SnapshotHandler(bool hasMachinePosition) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(7125, request.RequestUri!.Port);
            Assert.Equal("/printer/objects/query", request.RequestUri.AbsolutePath);
            Assert.Equal("?webhooks=state&print_stats=state&toolhead=homed_axes&gcode_move=position,gcode_position", request.RequestUri.Query);
            Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            string machine = hasMachinePosition ? "\"position\":[11.25,22.5,30.125,0]," : string.Empty;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"result":{"status":{"webhooks":{"state":"ready"},"print_stats":{"state":"standby"},"toolhead":{"homed_axes":"xyz"},"gcode_move":{MACHINE"gcode_position":[1.25,2.5,0.125,0],"homing_origin":[900,900,900,0]}}}}
                    """.Replace("MACHINE", machine, StringComparison.Ordinal), Encoding.UTF8, "application/json"),
            });
        }
    }
}
