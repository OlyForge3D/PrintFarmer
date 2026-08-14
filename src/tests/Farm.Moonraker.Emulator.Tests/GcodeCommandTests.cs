using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

/// <summary>
/// Direct (emulator-only, no real backend client) coverage of every gcode command the
/// real <c>MoonrakerClient</c> actually sends through <c>/printer/gcode/script</c>:
/// <c>M112</c>, <c>FIRMWARE_RESTART</c>/<c>RESTART</c>, <c>M104</c>/<c>M140</c>,
/// <c>G28</c> (bare/partial), and both relative/absolute move shapes. Also covers the
/// documented no-op boundary for <c>M84</c>/<c>LOAD_FILAMENT</c>/<c>UNLOAD_FILAMENT</c>/<c>M600</c>.
/// </summary>
public sealed class GcodeCommandTests : IClassFixture<ReadyPrinterFactory>
{
    private readonly ReadyPrinterFactory _factory;

    public GcodeCommandTests(ReadyPrinterFactory factory) => _factory = factory;

    private async Task<HttpClient> ClientWithScenarioAsync(string scenario)
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/__emulator/printer/scenario",
            TestRequests.Json($$"""{"scenario":"{{scenario}}"}"""));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    private static async Task<JsonElement> QueryObjectsAsync(HttpClient client, string query)
    {
        using HttpResponseMessage response = await client.GetAsync($"/printer/objects/query?{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("result").GetProperty("status").Clone();
    }

    private static Task<HttpResponseMessage> SendScriptAsync(HttpClient client, string script) =>
        client.PostAsync("/printer/gcode/script", TestRequests.Json(JsonSerializer.Serialize(new { script })));

    [Fact]
    public async Task M112_TransitionsKlippyToShutdownAndPrintStateToError()
    {
        using HttpClient client = await ClientWithScenarioAsync("Ready");

        using HttpResponseMessage response = await SendScriptAsync(client, "M112");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage info = await client.GetAsync("/printer/info");
        using JsonDocument infoDoc = JsonDocument.Parse(await info.Content.ReadAsStringAsync());
        infoDoc.RootElement.GetProperty("result").GetProperty("state").GetString().Should().Be("shutdown");

        // print_stats itself is only reachable while Klippy is ready, matching real
        // Moonraker's 503 behavior once Klippy has shut down.
        using HttpResponseMessage query = await client.GetAsync("/printer/objects/query?print_stats");
        query.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        await client.PostAsync("/__emulator/printer/reset", content: null);
    }

    [Theory]
    [InlineData("FIRMWARE_RESTART")]
    [InlineData("RESTART")]
    public async Task FirmwareRestartOrRestart_RecoversKlippyAndClearsPrintJob(string recoveryCommand)
    {
        using HttpClient client = await ClientWithScenarioAsync("Printing");

        using HttpResponseMessage estop = await SendScriptAsync(client, "M112");
        estop.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage recover = await SendScriptAsync(client, recoveryCommand);
        recover.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage info = await client.GetAsync("/printer/info");
        using JsonDocument infoDoc = JsonDocument.Parse(await info.Content.ReadAsStringAsync());
        infoDoc.RootElement.GetProperty("result").GetProperty("state").GetString().Should().Be("ready");

        JsonElement status = await QueryObjectsAsync(client, "print_stats&toolhead");
        status.GetProperty("print_stats").GetProperty("state").GetString().Should().Be("standby");
        status.GetProperty("print_stats").GetProperty("filename").GetString().Should().BeEmpty();

        // A real MCU reboot loses homing.
        status.GetProperty("toolhead").GetProperty("homed_axes").GetString().Should().BeEmpty();

        await client.PostAsync("/__emulator/printer/reset", content: null);
    }

    [Fact]
    public async Task M104_SetsExtruderTarget()
    {
        using HttpClient client = await ClientWithScenarioAsync("Ready");

        using HttpResponseMessage response = await SendScriptAsync(client, "M104 S215");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement status = await QueryObjectsAsync(client, "extruder");
        status.GetProperty("extruder").GetProperty("target").GetDouble().Should().Be(215);
    }

    [Fact]
    public async Task M140_SetsBedTarget()
    {
        using HttpClient client = await ClientWithScenarioAsync("Ready");

        using HttpResponseMessage response = await SendScriptAsync(client, "M140 S60");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement status = await QueryObjectsAsync(client, "heater_bed");
        status.GetProperty("heater_bed").GetProperty("target").GetDouble().Should().Be(60);
    }

    [Fact]
    public async Task CombinedSetTemps_BothLinesInOneScript_SetBothTargets()
    {
        using HttpClient client = await ClientWithScenarioAsync("Ready");

        // Matches MoonrakerClient.SetTempsAsync's exact shape: both commands newline-joined into one script.
        using HttpResponseMessage response = await SendScriptAsync(client, "M104 S200\nM140 S55");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement status = await QueryObjectsAsync(client, "extruder&heater_bed");
        status.GetProperty("extruder").GetProperty("target").GetDouble().Should().Be(200);
        status.GetProperty("heater_bed").GetProperty("target").GetDouble().Should().Be(55);
    }

    [Fact]
    public async Task G28_Bare_HomesAllAxes()
    {
        using HttpClient client = await ClientWithScenarioAsync("Ready");
        await SendScriptAsync(client, "FIRMWARE_RESTART"); // clears homed_axes first

        using HttpResponseMessage response = await SendScriptAsync(client, "G28");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement status = await QueryObjectsAsync(client, "toolhead");
        status.GetProperty("toolhead").GetProperty("homed_axes").GetString().Should().Be("xyz");

        await client.PostAsync("/__emulator/printer/reset", content: null);
    }

    [Fact]
    public async Task G28XY_HomesOnlyXAndY()
    {
        using HttpClient client = await ClientWithScenarioAsync("Ready");
        await SendScriptAsync(client, "FIRMWARE_RESTART"); // clears homed_axes first

        using HttpResponseMessage response = await SendScriptAsync(client, "G28 X Y");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement status = await QueryObjectsAsync(client, "toolhead");
        status.GetProperty("toolhead").GetProperty("homed_axes").GetString().Should().Be("xy");

        await client.PostAsync("/__emulator/printer/reset", content: null);
    }

    [Fact]
    public async Task G28Z_HomesOnlyZAndAccumulatesWithPriorAxes()
    {
        using HttpClient client = await ClientWithScenarioAsync("Ready");
        await SendScriptAsync(client, "FIRMWARE_RESTART"); // clears homed_axes first
        await SendScriptAsync(client, "G28 X Y");

        using HttpResponseMessage response = await SendScriptAsync(client, "G28 Z");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement status = await QueryObjectsAsync(client, "toolhead");
        status.GetProperty("toolhead").GetProperty("homed_axes").GetString().Should().Be("xyz");

        await client.PostAsync("/__emulator/printer/reset", content: null);
    }

    [Fact]
    public async Task G28_WhilePrinting_Returns409Busy()
    {
        using HttpClient client = await ClientWithScenarioAsync("Printing");

        using HttpResponseMessage response = await SendScriptAsync(client, "G28 Z");
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await client.PostAsync("/__emulator/printer/reset", content: null);
    }

    [Fact]
    public async Task RelativeMove_MatchingMoveAsyncShape_UpdatesPositionAndRestoresAbsoluteMode()
    {
        using HttpClient client = await ClientWithScenarioAsync("Ready");
        JsonElement before = await QueryObjectsAsync(client, "toolhead");
        double[] beforePosition = before.GetProperty("toolhead").GetProperty("position")
            .EnumerateArray().Select(e => e.GetDouble()).ToArray();

        // Exact shape MoonrakerClient.MoveAsync sends: one combined "G91 G0 ..." line
        // then a bare "G90" line restoring absolute mode, joined by newline.
        using HttpResponseMessage response = await SendScriptAsync(client, "G91 G0 X10 Y-5 Z0.5 F3000\nG90");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement after = await QueryObjectsAsync(client, "toolhead&gcode_move");
        double[] afterPosition = after.GetProperty("toolhead").GetProperty("position")
            .EnumerateArray().Select(e => e.GetDouble()).ToArray();

        afterPosition[0].Should().BeApproximately(beforePosition[0] + 10, 0.001);
        afterPosition[1].Should().BeApproximately(beforePosition[1] - 5, 0.001);
        afterPosition[2].Should().BeApproximately(beforePosition[2] + 0.5, 0.001);
        after.GetProperty("gcode_move").GetProperty("absolute_coordinates").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AbsoluteMove_MatchingMoveToAsyncShape_SetsPositionDirectly()
    {
        using HttpClient client = await ClientWithScenarioAsync("Ready");

        // Exact shape MoonrakerClient.MoveToAsync sends: a single "G90 G0 ..." line.
        using HttpResponseMessage response = await SendScriptAsync(client, "G90 G0 X42 Y17 Z3 F1500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement status = await QueryObjectsAsync(client, "toolhead&gcode_move");
        double[] position = status.GetProperty("toolhead").GetProperty("position")
            .EnumerateArray().Select(e => e.GetDouble()).ToArray();

        position[0].Should().BeApproximately(42, 0.001);
        position[1].Should().BeApproximately(17, 0.001);
        position[2].Should().BeApproximately(3, 0.001);
        status.GetProperty("gcode_move").GetProperty("absolute_coordinates").GetBoolean().Should().BeTrue();

        await client.PostAsync("/__emulator/printer/reset", content: null);
    }

    [Theory]
    [InlineData("M84")]
    [InlineData("LOAD_FILAMENT")]
    [InlineData("UNLOAD_FILAMENT")]
    [InlineData("M600")]
    public async Task AcknowledgedNoOpCommands_SucceedWithoutObservableStateChange(string command)
    {
        // Documented fidelity boundary: these commands are accepted (still pass through
        // the Klippy-ready gate and any active fault rules) but intentionally do not
        // mutate any state this emulator tracks, because the currently consuming UI has
        // no "motors enabled" or "filament loaded" flag to assert against. If that ever
        // changes, extend PrinterAggregate.SendGcode alongside the new observable field.
        using HttpClient client = await ClientWithScenarioAsync("Ready");
        JsonElement before = await QueryObjectsAsync(client, "toolhead&extruder&heater_bed");

        using HttpResponseMessage response = await SendScriptAsync(client, command);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement after = await QueryObjectsAsync(client, "toolhead&extruder&heater_bed");
        after.GetProperty("toolhead").GetProperty("homed_axes").GetString()
            .Should().Be(before.GetProperty("toolhead").GetProperty("homed_axes").GetString());
        after.GetProperty("extruder").GetProperty("target").GetDouble()
            .Should().Be(before.GetProperty("extruder").GetProperty("target").GetDouble());
        after.GetProperty("heater_bed").GetProperty("target").GetDouble()
            .Should().Be(before.GetProperty("heater_bed").GetProperty("target").GetDouble());
    }

    [Fact]
    public async Task M112_BroadcastsNotifyKlippyShutdownToSubscribedConnection()
    {
        using HttpClient client = await ClientWithScenarioAsync("Ready");

        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        using WebSocket socket = await wsClient.ConnectAsync(new Uri("ws://localhost/websocket"), CancellationToken.None);
        await SendAsync(socket, """{"jsonrpc":"2.0","method":"server.connection.identify","id":1}""");
        _ = await ReceiveAsync(socket);

        using HttpResponseMessage response = await SendScriptAsync(client, "M112");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument notification = await ReceiveAsync(socket);
        notification.RootElement.GetProperty("method").GetString().Should().Be("notify_klippy_shutdown");

        await client.PostAsync("/__emulator/printer/reset", content: null);
    }

    [Fact]
    public async Task FirmwareRestart_BroadcastsNotifyKlippyReadyToSubscribedConnection()
    {
        using HttpClient client = await ClientWithScenarioAsync("Shutdown");

        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        using WebSocket socket = await wsClient.ConnectAsync(new Uri("ws://localhost/websocket"), CancellationToken.None);
        await SendAsync(socket, """{"jsonrpc":"2.0","method":"server.connection.identify","id":1}""");
        _ = await ReceiveAsync(socket);

        using HttpResponseMessage response = await SendScriptAsync(client, "FIRMWARE_RESTART");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument notification = await ReceiveAsync(socket);
        notification.RootElement.GetProperty("method").GetString().Should().Be("notify_klippy_ready");

        await client.PostAsync("/__emulator/printer/reset", content: null);
    }

    private static async Task SendAsync(WebSocket socket, string json) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonDocument> ReceiveAsync(WebSocket socket)
    {
        byte[] buffer = new byte[32 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(stream.ToArray());
    }
}
