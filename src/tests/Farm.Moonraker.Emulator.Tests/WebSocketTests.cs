using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

public sealed class WebSocketTests : IClassFixture<ReadyPrinterFactory>
{
    private readonly ReadyPrinterFactory _factory;

    public WebSocketTests(ReadyPrinterFactory factory) => _factory = factory;

    private async Task<WebSocket> ConnectAsync()
    {
        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        return await wsClient.ConnectAsync(new Uri("ws://localhost/websocket"), CancellationToken.None);
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

    private async Task ResetPrinterAsync()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync("/__emulator/printer/reset", content: null);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ConnectionIdentify_ReturnsResultAndEchoesId()
    {
        using WebSocket socket = await ConnectAsync();
        await SendAsync(socket, """{"jsonrpc":"2.0","method":"server.connection.identify","params":{"client_name":"test","version":"1.0","type":"web_client"},"id":100}""");
        using JsonDocument doc = await ReceiveAsync(socket);

        doc.RootElement.GetProperty("id").GetInt32().Should().Be(100);
        doc.RootElement.GetProperty("result").GetProperty("connection_id").GetInt32().Should().Be(1);
        doc.RootElement.TryGetProperty("error", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ObjectsList_ReturnsKnownKlipperObjectNames()
    {
        using WebSocket socket = await ConnectAsync();
        await SendAsync(socket, """{"jsonrpc":"2.0","method":"printer.objects.list","params":{},"id":102}""");
        using JsonDocument doc = await ReceiveAsync(socket);

        string[] objects = doc.RootElement.GetProperty("result").GetProperty("objects")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        objects.Should().Contain(["print_stats", "toolhead", "extruder", "heater_bed", "exclude_object", "webhooks"]);
    }

    [Fact]
    public async Task ObjectsSubscribe_ReturnsInitialStatusForRequestedObjectsOnly()
    {
        await ResetPrinterAsync();
        using WebSocket socket = await ConnectAsync();
        await SendAsync(
            socket,
            """{"jsonrpc":"2.0","method":"printer.objects.subscribe","params":{"objects":{"print_stats":null,"extruder":["temperature"]}},"id":101}""");
        using JsonDocument doc = await ReceiveAsync(socket);

        JsonElement status = doc.RootElement.GetProperty("result").GetProperty("status");
        status.GetProperty("print_stats").GetProperty("state").GetString().Should().Be("standby");
        status.GetProperty("extruder").TryGetProperty("temperature", out _).Should().BeTrue();
        status.GetProperty("extruder").TryGetProperty("target", out _).Should().BeFalse();
        status.TryGetProperty("heater_bed", out _).Should().BeFalse();
        doc.RootElement.GetProperty("result").GetProperty("eventtime").GetDouble().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ObjectsQuery_ReturnsFilteredSnapshotWithoutPersistingSubscription()
    {
        await ResetPrinterAsync();
        using WebSocket socket = await ConnectAsync();
        await SendAsync(
            socket,
            """{"jsonrpc":"2.0","method":"printer.objects.query","params":{"objects":{"print_stats":null}},"id":5}""");
        using JsonDocument doc = await ReceiveAsync(socket);

        doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats").GetProperty("state")
            .GetString().Should().Be("standby");
    }

    [Fact]
    public async Task CameraStartAndStopMonitor_ReturnEmptyResult()
    {
        using WebSocket socket = await ConnectAsync();

        await SendAsync(socket, """{"jsonrpc":"2.0","method":"camera.start_monitor","id":3}""");
        using JsonDocument startDoc = await ReceiveAsync(socket);
        startDoc.RootElement.GetProperty("id").GetInt32().Should().Be(3);
        startDoc.RootElement.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Object);

        await SendAsync(socket, """{"jsonrpc":"2.0","method":"camera.stop_monitor","id":4}""");
        using JsonDocument stopDoc = await ReceiveAsync(socket);
        stopDoc.RootElement.GetProperty("id").GetInt32().Should().Be(4);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsJsonRpcMethodNotFoundError()
    {
        using WebSocket socket = await ConnectAsync();
        await SendAsync(socket, """{"jsonrpc":"2.0","method":"totally.unknown.method","id":42}""");
        using JsonDocument doc = await ReceiveAsync(socket);

        doc.RootElement.GetProperty("id").GetInt32().Should().Be(42);
        doc.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32601);
        doc.RootElement.TryGetProperty("result", out _).Should().BeFalse();
    }

    [Fact]
    public async Task MalformedClientJson_ReturnsParseError()
    {
        using WebSocket socket = await ConnectAsync();
        await SendAsync(socket, "{ not valid json ");
        using JsonDocument doc = await ReceiveAsync(socket);

        doc.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32700);
    }

    [Fact]
    public async Task NotifyStatusUpdate_BroadcastsAfterGcodeMutation_ToSubscribedConnectionOnly()
    {
        await ResetPrinterAsync();
        using HttpClient client = _factory.CreateClient();
        await client.PostAsync("/__emulator/printer/scenario", TestRequests.Json("""{"scenario":"Printing"}"""));

        using WebSocket subscribed = await ConnectAsync();
        await SendAsync(
            subscribed,
            """{"jsonrpc":"2.0","method":"printer.objects.subscribe","params":{"objects":{"exclude_object":null}},"id":1}""");
        _ = await ReceiveAsync(subscribed); // initial subscribe response

        using WebSocket unsubscribed = await ConnectAsync();
        await SendAsync(unsubscribed, """{"jsonrpc":"2.0","method":"server.connection.identify","id":2}""");
        _ = await ReceiveAsync(unsubscribed); // identify response only, never subscribes

        using HttpResponseMessage gcode = await client.PostAsync(
            "/printer/gcode/script",
            TestRequests.Json("""{"script":"EXCLUDE_OBJECT NAME=benchy_cabin"}"""));
        gcode.EnsureSuccessStatusCode();

        using JsonDocument notification = await ReceiveAsync(subscribed);
        notification.RootElement.GetProperty("method").GetString().Should().Be("notify_status_update");
        notification.RootElement.TryGetProperty("id", out _).Should().BeFalse();
        JsonElement paramsArray = notification.RootElement.GetProperty("params");
        paramsArray[0].GetProperty("exclude_object").GetProperty("excluded_objects")
            .EnumerateArray().Select(e => e.GetString()).Should().Contain("benchy_cabin");

        await ResetPrinterAsync();
    }

    [Fact]
    public async Task NotifyKlippyShutdown_BroadcastsWhenScenarioTransitionsToShutdown()
    {
        await ResetPrinterAsync();
        using HttpClient client = _factory.CreateClient();

        using WebSocket socket = await ConnectAsync();
        await SendAsync(socket, """{"jsonrpc":"2.0","method":"server.connection.identify","id":1}""");
        _ = await ReceiveAsync(socket);

        using HttpResponseMessage scenario = await client.PostAsync(
            "/__emulator/printer/scenario",
            TestRequests.Json("""{"scenario":"Shutdown"}"""));
        scenario.EnsureSuccessStatusCode();

        using JsonDocument notification = await ReceiveAsync(socket);
        notification.RootElement.GetProperty("method").GetString().Should().Be("notify_klippy_shutdown");

        // restore for other tests sharing this printer
        await ResetPrinterAsync();
    }

    [Fact]
    public async Task IndependentSubscribers_EachReceiveOnlyTheirOwnFilteredFields()
    {
        await ResetPrinterAsync();
        using HttpClient client = _factory.CreateClient();
        await client.PostAsync("/__emulator/printer/scenario", TestRequests.Json("""{"scenario":"Printing"}"""));

        using WebSocket subA = await ConnectAsync();
        await SendAsync(subA, """{"jsonrpc":"2.0","method":"printer.objects.subscribe","params":{"objects":{"extruder":null}},"id":1}""");
        _ = await ReceiveAsync(subA);

        using WebSocket subB = await ConnectAsync();
        await SendAsync(subB, """{"jsonrpc":"2.0","method":"printer.objects.subscribe","params":{"objects":{"heater_bed":null}},"id":1}""");
        _ = await ReceiveAsync(subB);

        using HttpResponseMessage gcode = await client.PostAsync(
            "/printer/gcode/script",
            TestRequests.Json("""{"script":"M117 hello"}"""));
        gcode.EnsureSuccessStatusCode();

        using JsonDocument notifyA = await ReceiveAsync(subA);
        using JsonDocument notifyB = await ReceiveAsync(subB);

        notifyA.RootElement.GetProperty("params")[0].TryGetProperty("extruder", out _).Should().BeTrue();
        notifyA.RootElement.GetProperty("params")[0].TryGetProperty("heater_bed", out _).Should().BeFalse();
        notifyB.RootElement.GetProperty("params")[0].TryGetProperty("heater_bed", out _).Should().BeTrue();
        notifyB.RootElement.GetProperty("params")[0].TryGetProperty("extruder", out _).Should().BeFalse();

        await ResetPrinterAsync();
    }
}
