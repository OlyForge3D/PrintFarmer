using System.Globalization;
using System.Net;
using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

public sealed class PluginSemanticControlTests
{
    [Theory]
    [InlineData(true, -5, "SET_GCODE_OFFSET Z=-5.000", "SAVE_CONFIG")]
    [InlineData(true, 5, "SET_GCODE_OFFSET Z=5.000", "SAVE_CONFIG")]
    [InlineData(false, -5, "M851 Z-5.000", "M500")]
    [InlineData(false, 5, "M851 Z5.000", "M500")]
    public async Task SaveZOffsetAsync_ValidOffset_PreservesSequentialBackendCommands(
        bool moonraker, int offset, string firstCommand, string secondCommand)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        ISupportsZOffsetCalibration client = moonraker
            ? new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings())
            : new OctoPrintClient(http);

        Assert.True(await client.SaveZOffsetAsync("http://printer.local:5237", offset, new PrinterCredential { ApiKey = "test-key" }, CancellationToken.None));

        string property = moonraker ? "script" : "command";
        Assert.Equal(
            [firstCommand, secondCommand],
            handler.Requests.Select(request => request.Body!.Value.GetProperty(property).GetString()));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("test-key", request.ApiKey);
            Assert.Equal(5237, request.Uri.Port);
            Assert.Equal(moonraker ? "/printer/gcode/script" : "/api/printer/command", request.Uri.AbsolutePath);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveZOffsetAsync_FirstCommandFails_DoesNotSendPersistenceCommand(bool moonraker)
    {
        using var handler = new CaptureHandler(HttpStatusCode.BadRequest);
        using var http = new HttpClient(handler);
        ISupportsZOffsetCalibration client = moonraker
            ? new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings())
            : new OctoPrintClient(http);

        Assert.False(await client.SaveZOffsetAsync("http://printer.local:5237", 1.25m, null, CancellationToken.None));

        Request request = Assert.Single(handler.Requests);
        Assert.Equal(
            moonraker ? "SET_GCODE_OFFSET Z=1.250" : "M851 Z1.250",
            request.Body!.Value.GetProperty(moonraker ? "script" : "command").GetString());
    }

    [Theory]
    [InlineData(true, "-5.001")]
    [InlineData(true, "5.001")]
    [InlineData(false, "-5.001")]
    [InlineData(false, "5.001")]
    public async Task SaveZOffsetAsync_InvalidOffset_RejectsBeforeSending(bool moonraker, string offset)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        ISupportsZOffsetCalibration client = moonraker
            ? new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings())
            : new OctoPrintClient(http);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.SaveZOffsetAsync("http://printer.local:5237", decimal.Parse(offset, CultureInfo.InvariantCulture), null, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(MmuControlProtocol.HappyHare, MmuControlAction.ChangeTool, 0, null, null, "MMU_CHANGE_TOOL TOOL=0")]
    [InlineData(MmuControlProtocol.HappyHare, MmuControlAction.ChangeTool, 16, null, null, "MMU_CHANGE_TOOL TOOL=16")]
    [InlineData(MmuControlProtocol.HappyHare, MmuControlAction.SelectTool, 16, null, null, "MMU_SELECT_TOOL TOOL=16")]
    [InlineData(MmuControlProtocol.HappyHare, MmuControlAction.Home, null, null, null, "MMU_HOME")]
    [InlineData(MmuControlProtocol.HappyHare, MmuControlAction.Recover, null, null, null, "MMU_RECOVER")]
    [InlineData(MmuControlProtocol.HappyHare, MmuControlAction.Load, null, null, null, "MMU_LOAD")]
    [InlineData(MmuControlProtocol.HappyHare, MmuControlAction.Eject, null, null, null, "MMU_EJECT")]
    [InlineData(MmuControlProtocol.Qidibox, MmuControlAction.Load, null, 0, null, "T0")]
    [InlineData(MmuControlProtocol.Qidibox, MmuControlAction.Unload, null, 16, null, "UNLOAD_T16")]
    [InlineData(MmuControlProtocol.Qidibox, MmuControlAction.Eject, null, 16, null, "EJECT_T16")]
    [InlineData(MmuControlProtocol.Afc, MmuControlAction.Load, null, null, "lane_1-A", "CHANGE_TOOL LANE=lane_1-A")]
    [InlineData(MmuControlProtocol.Afc, MmuControlAction.Unload, null, null, "lane_1-A", "TOOL_UNLOAD LANE=lane_1-A")]
    public async Task MoonrakerMmuControls_AuthenticatedBackend_PreserveAllowlistedCommands(
        MmuControlProtocol protocol, MmuControlAction action, int? tool, int? gate, string? lane, string command)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());
        var credential = new PrinterCredential { ApiKey = "test-key" };
        const string url = "http://printer.local:7125/moonraker";

        Assert.True(await client.ExecuteMmuAsync(url, new MmuControlRequest(action, protocol, tool, gate, lane), credential, CancellationToken.None));

        Request request = Assert.Single(handler.Requests);
        Assert.Equal(command, request.Body!.Value.GetProperty("script").GetString());
        Assert.Equal(7125, request.Uri.Port);
        Assert.Equal("/moonraker/printer/gcode/script", request.Uri.AbsolutePath);
        Assert.Equal("test-key", request.ApiKey);
        Assert.Equal(HttpMethod.Post, request.Method);
    }

    [Theory]
    [InlineData(MmuControlProtocol.HappyHare, MmuControlAction.ChangeTool, -1, null, null)]
    [InlineData(MmuControlProtocol.HappyHare, MmuControlAction.SelectTool, 17, null, null)]
    [InlineData(MmuControlProtocol.HappyHare, MmuControlAction.ChangeTool, null, null, null)]
    [InlineData(MmuControlProtocol.HappyHare, MmuControlAction.Unload, null, null, null)]
    [InlineData(MmuControlProtocol.Qidibox, MmuControlAction.Load, null, -1, null)]
    [InlineData(MmuControlProtocol.Qidibox, MmuControlAction.Eject, null, 17, null)]
    [InlineData(MmuControlProtocol.Qidibox, MmuControlAction.Home, null, 0, null)]
    [InlineData(MmuControlProtocol.Afc, MmuControlAction.Eject, null, null, "lane1")]
    [InlineData(MmuControlProtocol.Afc, MmuControlAction.Load, null, null, "")]
    [InlineData(MmuControlProtocol.Afc, MmuControlAction.Load, null, null, "lane1\nM112")]
    [InlineData(MmuControlProtocol.Afc, MmuControlAction.Load, null, null, "lane1\n")]
    [InlineData(MmuControlProtocol.Afc, MmuControlAction.Load, null, null, "lane1 MOVE=1")]
    [InlineData((MmuControlProtocol)999, MmuControlAction.Load, null, null, null)]
    [InlineData(MmuControlProtocol.HappyHare, (MmuControlAction)999, null, null, null)]
    public async Task MoonrakerMmuControls_InvalidRequest_DoNotSend(
        MmuControlProtocol protocol, MmuControlAction action, int? tool, int? gate, string? lane)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ExecuteMmuAsync("http://printer.local:7125", new MmuControlRequest(action, protocol, tool, gate, lane), null, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public async Task MoonrakerMmuControls_LaneLength_EnforcesBound(int length, bool supported)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());
        var request = new MmuControlRequest(MmuControlAction.Load, MmuControlProtocol.Afc, LaneName: new string('a', length));

        if (supported)
        {
            Assert.True(await client.ExecuteMmuAsync("http://printer.local:7125", request, null, CancellationToken.None));
            Assert.Single(handler.Requests);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                client.ExecuteMmuAsync("http://printer.local:7125", request, null, CancellationToken.None));
            Assert.Empty(handler.Requests);
        }
    }

    [Theory]
    [InlineData("http://printer.local:7125", 7125, "")]
    [InlineData("https://printer.local:9443/moonraker", 9443, "/moonraker")]
    public async Task MoonrakerControls_ConfiguredBackend_PreserveCredentialsAndTranslateCommands(string baseUrl, int port, string prefix)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());
        var credential = new PrinterCredential { ApiKey = "test-key" };
        ISupportsMovement movement = client;
        ISupportsTemperatureControl temperature = client;
        MoonrakerClient motors = client;
        MoonrakerClient extrusion = client;
        MoonrakerClient emergency = client;

        Assert.True(await movement.HomeAsync(baseUrl, credential, CancellationToken.None));
        Assert.True(await movement.HomeXYAsync(baseUrl, credential, CancellationToken.None));
        Assert.True(await movement.HomeZAsync(baseUrl, credential, CancellationToken.None));
        Assert.True(await temperature.SetTemperaturesAsync(baseUrl, 205, 60, credential, CancellationToken.None));
        Assert.True(await motors.DisableMotorsAsync(baseUrl, credential, CancellationToken.None));
        Assert.True(await extrusion.ExtrudeAsync(baseUrl, -2.5, 300, credential, CancellationToken.None));
        Assert.True(await emergency.EmergencyStopAsync(baseUrl, credential, CancellationToken.None));

        Assert.Equal(7, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(port, request.Uri.Port);
            Assert.Equal("test-key", request.ApiKey);
            Assert.Equal(HttpMethod.Post, request.Method);
        });
        Assert.Equal(
            ["G28", "G28 X Y", "G28 Z", "M104 S205\nM140 S60", "M84"],
            handler.Requests.Take(5).Select(request => request.Body!.Value.GetProperty("script").GetString()));
        Request extrude = handler.Requests[5];
        Assert.Equal(
            "M83\nG1 E-2.5 F300\nM82",
            extrude.Body!.Value.GetProperty("script").GetString());
        Assert.All(handler.Requests.Take(6), request =>
            Assert.Equal(prefix + "/printer/gcode/script", request.Uri.AbsolutePath));
        Assert.Equal(prefix + "/printer/emergency_stop", handler.Requests[6].Uri.AbsolutePath);
        Assert.Null(handler.Requests[6].Body);
        Assert.True(client.ControlCapabilities.SupportsDisableMotors);
        Assert.True(client.ControlCapabilities.SupportsExtrusion);
    }

    [Theory]
    [InlineData("home", "x,y,z")]
    [InlineData("xy", "x,y")]
    [InlineData("z", "z")]
    public async Task OctoPrintHoming_AuthenticatedBackend_UsesNativeAxisEndpoint(string operation, string axes)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = new OctoPrintClient(http);
        OctoPrintClient movement = client;
        var credential = new PrinterCredential { ApiKey = "test-key" };
        const string url = "http://printer.local:5000/octoprint";
        bool success = operation switch
        {
            "home" => await movement.HomeAsync(url, credential, CancellationToken.None),
            "xy" => await movement.HomeXYAsync(url, credential, CancellationToken.None),
            _ => await movement.HomeZAsync(url, credential, CancellationToken.None),
        };

        Assert.True(success);
        Request request = Assert.Single(handler.Requests);
        Assert.Equal(5000, request.Uri.Port);
        Assert.Equal("/octoprint/api/printer/printhead", request.Uri.AbsolutePath);
        Assert.Equal("test-key", request.ApiKey);
        Assert.Equal("home", request.Body!.Value.GetProperty("command").GetString());
        Assert.Equal(axes.Split(','), request.Body.Value.GetProperty("axes").EnumerateArray().Select(axis => axis.GetString()));
        Assert.True(client.ControlCapabilities.SupportsHoming);
        Assert.True(client.ControlCapabilities.SupportsHomingXY);
        Assert.True(client.ControlCapabilities.SupportsHomingZ);
    }

    [Fact]
    public async Task OctoPrintTemperature_GenericInterface_UsesNativeHeaterEndpoints()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = new OctoPrintClient(http);
        OctoPrintClient temperature = client;

        Assert.True(await temperature.SetTemperaturesAsync(
            "http://printer.local:5000", 210, 65,
            new PrinterCredential { ApiKey = "test-key" }, CancellationToken.None));

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(5000, request.Uri.Port);
            Assert.Equal("test-key", request.ApiKey);
            Assert.Equal("target", request.Body!.Value.GetProperty("command").GetString());
        });
        Assert.Equal("/api/printer/bed", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal(65, handler.Requests[0].Body!.Value.GetProperty("target").GetInt32());
        Assert.Equal("/api/printer/tool", handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal(210, handler.Requests[1].Body!.Value.GetProperty("targets").GetProperty("tool0").GetInt32());
        Assert.True(client.ControlCapabilities.SupportsHotendTemperature);
        Assert.True(client.ControlCapabilities.SupportsBedTemperature);
        Assert.False(client.ControlCapabilities.SupportsExtrusion);
        Assert.False(client.ControlCapabilities.SupportsDisableMotors);
    }

    [Fact]
    public async Task OctoPrintEmergencyStop_AuthenticatedBackend_PreservesCommandTransport()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = new OctoPrintClient(http);

        Assert.True(await client.EmergencyStopAsync(
            "http://printer.local:5000",
            new PrinterCredential { ApiKey = "test-key" }, CancellationToken.None));

        Request request = Assert.Single(handler.Requests);
        Assert.Equal("/api/printer/command", request.Uri.AbsolutePath);
        Assert.Equal(5000, request.Uri.Port);
        Assert.Equal("test-key", request.ApiKey);
        Assert.Equal("M112", request.Body!.Value.GetProperty("command").GetString());
    }

    [Fact]
    public async Task PrusaLinkControls_DigestCredentials_UseSemanticLegacyEndpoints()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = new PrusaLinkClient(http);
        var credential = new PrinterCredential { Username = "test-user", Password = "test-password", ApiKey = "test-key" };
        const string url = "http://printer.local:8080/";

        Assert.True(await client.HomeAsync(url, credential, CancellationToken.None));
        Assert.True(await client.HomeXYAsync(url, credential, CancellationToken.None));
        Assert.True(await client.HomeZAsync(url, credential, CancellationToken.None));
        Assert.True(await client.SetTemperaturesAsync(url, 210, 60, credential, CancellationToken.None));

        Assert.Equal(5, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(8080, request.Uri.Port);
            Assert.Equal("test-key", request.ApiKey);
        });
        string[][] axes = [["x", "y", "z"], ["x", "y"], ["z"]];
        for (int index = 0; index < axes.Length; index++)
        {
            Request request = handler.Requests[index];
            Assert.Equal("/api/printer/printhead", request.Uri.AbsolutePath);
            Assert.Equal("home", request.Body!.Value.GetProperty("command").GetString());
            Assert.Equal(axes[index], request.Body.Value.GetProperty("axes").EnumerateArray().Select(axis => axis.GetString()));
        }

        Assert.Equal("/api/printer/tool", handler.Requests[3].Uri.AbsolutePath);
        Assert.Equal(210, handler.Requests[3].Body!.Value.GetProperty("targets").GetProperty("tool0").GetInt32());
        Assert.Equal("/api/printer/bed", handler.Requests[4].Uri.AbsolutePath);
        Assert.Equal(60, handler.Requests[4].Body!.Value.GetProperty("target").GetInt32());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SemanticControls_CallerCancelled_DoNotSend(bool moonraker)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        ISupportsTemperatureControl client = moonraker
            ? new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings())
            : new OctoPrintClient(http);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SetTemperaturesAsync("http://printer.local:5000", 210, 60, null, cancelled.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PrusaLinkControls_MissingDigestCredentials_FailWithoutSending()
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var client = new PrusaLinkClient(http);

        Assert.False(await client.HomeAsync("http://printer.local:8080", null, CancellationToken.None));
        Assert.False(await client.HomeXYAsync("http://printer.local:8080", null, CancellationToken.None));
        Assert.False(await client.HomeZAsync("http://printer.local:8080", null, CancellationToken.None));
        Assert.False(await client.SetTemperaturesAsync("http://printer.local:8080", 210, 60, null, CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.False(client.ControlCapabilities.SupportsDisableMotors);
        Assert.False(client.ControlCapabilities.SupportsExtrusion);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"Printer is busy printing"}}""")]
    public async Task MoonrakerControls_BusyBackend_PreservesBusyException(HttpStatusCode status, string response)
    {
        using var handler = new CaptureHandler(status, response);
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());

        await Assert.ThrowsAsync<PrinterBackendBusyException>(() =>
            client.DisableMotorsAsync("http://printer.local:7125", null, CancellationToken.None));
    }

    private sealed record Request(Uri Uri, HttpMethod Method, string? ApiKey, JsonElement? Body);

    private sealed class CaptureHandler(HttpStatusCode status = HttpStatusCode.OK, string response = "{}") : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            JsonElement? body = null;
            if (request.Content is not null)
            {
                using JsonDocument document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                body = document.RootElement.Clone();
            }

            Requests.Add(new Request(
                request.RequestUri!, request.Method,
                request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? keys) ? keys.Single() : null, body));
            return new HttpResponseMessage(status) { Content = new StringContent(response) };
        }
    }
}
