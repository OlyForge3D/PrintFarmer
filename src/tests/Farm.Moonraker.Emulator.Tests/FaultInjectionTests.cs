using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

public sealed class FaultInjectionTests : IClassFixture<ReadyPrinterFactory>
{
    private readonly ReadyPrinterFactory _factory;

    public FaultInjectionTests(ReadyPrinterFactory factory) => _factory = factory;

    private async Task ClearRulesAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsync("/__emulator/rules/clear", content: null);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"rules/clear failed: {(int)response.StatusCode} {body}");
        }
    }

    [Fact]
    public async Task HttpLatencyRule_DelaysMatchingRequest()
    {
        using HttpClient client = _factory.CreateClient();
        await ClearRulesAsync(client);
        using HttpResponseMessage created = await client.PostAsync(
            "/__emulator/rules",
            TestRequests.Json("""{"printerId":"ready","target":"Http","effect":"Latency","pathContains":"/printer/info","latencyMs":300,"repeating":false,"remainingUses":1}"""));
        created.StatusCode.Should().Be(HttpStatusCode.OK);

        var stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage response = await client.GetAsync("/printer/info");
        stopwatch.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stopwatch.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(250);
    }

    [Fact]
    public async Task HttpLatencyRule_OneShot_DoesNotDelaySecondRequest()
    {
        using HttpClient client = _factory.CreateClient();
        await ClearRulesAsync(client);
        await client.PostAsync(
            "/__emulator/rules",
            TestRequests.Json("""{"printerId":"ready","target":"Http","effect":"Latency","pathContains":"/printer/info","latencyMs":300,"repeating":false,"remainingUses":1}"""));

        using HttpResponseMessage first = await client.GetAsync("/printer/info");
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage second = await client.GetAsync("/printer/info");
        stopwatch.Stop();

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(250);
    }

    [Fact]
    public async Task HttpStatusRule_ForcesConfiguredStatusCodeAndBody()
    {
        using HttpClient client = _factory.CreateClient();
        await ClearRulesAsync(client);
        await client.PostAsync(
            "/__emulator/rules",
            TestRequests.Json("""{"printerId":"ready","target":"Http","effect":"HttpStatus","pathContains":"/server/info","httpStatusCode":418,"httpBody":"{\"message\":\"teapot\"}","repeating":false,"remainingUses":1}"""));

        using HttpResponseMessage response = await client.GetAsync("/server/info");
        ((int)response.StatusCode).Should().Be(418);
        (await response.Content.ReadAsStringAsync()).Should().Contain("teapot");
    }

    [Fact]
    public async Task HttpMalformedJsonRule_ReturnsInvalidJsonBodyForMatchingRequest()
    {
        using HttpClient client = _factory.CreateClient();
        await ClearRulesAsync(client);
        await client.PostAsync(
            "/__emulator/rules",
            TestRequests.Json("""{"printerId":"ready","target":"Http","effect":"MalformedJson","pathContains":"/printer/info","repeating":false,"remainingUses":1}"""));

        using HttpResponseMessage response = await client.GetAsync("/printer/info");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        Action parse = () => JsonDocument.Parse(body);
        parse.Should().Throw<JsonException>("the injected fault must actually be structurally invalid JSON");
    }

    [Fact]
    public async Task KlippyUnavailableRule_Forces503ForMatchingHttpRequest()
    {
        using HttpClient client = _factory.CreateClient();
        await ClearRulesAsync(client);
        await client.PostAsync(
            "/__emulator/rules",
            TestRequests.Json("""{"printerId":"ready","target":"Http","effect":"KlippyUnavailable","pathContains":"/printer/gcode/script","repeating":false,"remainingUses":1}"""));

        using HttpResponseMessage response = await client.PostAsync(
            "/printer/gcode/script",
            TestRequests.Json("""{"script":"M117 hi"}"""));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Klippy is not connected");
    }

    [Fact]
    public async Task RepeatingRule_MatchesMultipleTimes()
    {
        using HttpClient client = _factory.CreateClient();
        await ClearRulesAsync(client);
        await client.PostAsync(
            "/__emulator/rules",
            TestRequests.Json("""{"printerId":"ready","target":"Http","effect":"HttpStatus","pathContains":"/machine/system_info","httpStatusCode":418,"repeating":true}"""));

        using HttpResponseMessage first = await client.GetAsync("/machine/system_info");
        using HttpResponseMessage second = await client.GetAsync("/machine/system_info");
        ((int)first.StatusCode).Should().Be(418);
        ((int)second.StatusCode).Should().Be(418);

        await ClearRulesAsync(client);
    }

    [Fact]
    public async Task RulesCrud_CreateListDeleteWorks()
    {
        using HttpClient client = _factory.CreateClient();
        await ClearRulesAsync(client);

        using HttpResponseMessage created = await client.PostAsync(
            "/__emulator/rules",
            TestRequests.Json("""{"printerId":"ready","target":"Http","effect":"HttpStatus","httpStatusCode":500,"repeating":true}"""));
        using JsonDocument createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        string ruleId = createdDoc.RootElement.GetProperty("id").GetString()!;

        using HttpResponseMessage list = await client.GetAsync("/__emulator/rules");
        using JsonDocument listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        listDoc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).Should().Contain(ruleId);

        using HttpResponseMessage deleted = await client.DeleteAsync($"/__emulator/rules/{ruleId}");
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage listAfter = await client.GetAsync("/__emulator/rules");
        using JsonDocument listAfterDoc = JsonDocument.Parse(await listAfter.Content.ReadAsStringAsync());
        listAfterDoc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).Should().NotContain(ruleId);
    }

    [Fact]
    public async Task InvalidRule_MissingRequiredField_Returns400()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/__emulator/rules",
            TestRequests.Json("""{"target":"Http","effect":"Latency","repeating":false}"""));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RpcErrorRule_ReturnsConfiguredJsonRpcError()
    {
        using HttpClient client = _factory.CreateClient();
        await ClearRulesAsync(client);
        await client.PostAsync(
            "/__emulator/rules",
            TestRequests.Json("""{"printerId":"ready","target":"WebSocket","effect":"RpcError","rpcMethod":"printer.objects.list","rpcErrorCode":-32001,"rpcErrorMessage":"Injected failure","repeating":false,"remainingUses":1}"""));

        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        using WebSocket socket = await wsClient.ConnectAsync(new Uri("ws://localhost/websocket"), CancellationToken.None);

        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"printer.objects.list","id":9}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        using JsonDocument doc = await ReceiveAsync(socket);

        doc.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32001);
        doc.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Be("Injected failure");
    }

    [Fact]
    public async Task WsDisconnectRule_ClosesConnectionAfterMatchingRequest()
    {
        using HttpClient client = _factory.CreateClient();
        await ClearRulesAsync(client);
        await client.PostAsync(
            "/__emulator/rules",
            TestRequests.Json("""{"printerId":"ready","target":"WebSocket","effect":"WsDisconnect","rpcMethod":"server.connection.identify","repeating":false,"remainingUses":1}"""));

        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        using WebSocket socket = await wsClient.ConnectAsync(new Uri("ws://localhost/websocket"), CancellationToken.None);

        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"server.connection.identify","id":1}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        _ = await ReceiveAsync(socket); // the response is still delivered before the forced close

        byte[] buffer = new byte[1024];
        WebSocketReceiveResult closeResult = await socket.ReceiveAsync(buffer, CancellationToken.None);
        closeResult.MessageType.Should().Be(WebSocketMessageType.Close);
    }

    [Fact]
    public async Task StaleNotificationsRule_SuppressesBroadcastsForConfiguredDuration()
    {
        using HttpClient client = _factory.CreateClient();
        await ClearRulesAsync(client);
        await client.PostAsync("/__emulator/printer/reset", content: null);

        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        using WebSocket socket = await wsClient.ConnectAsync(new Uri("ws://localhost/websocket"), CancellationToken.None);

        await socket.SendAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"printer.objects.subscribe","params":{"objects":{"print_stats":null}},"id":1}"""),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
        _ = await ReceiveAsync(socket);

        await client.PostAsync(
            "/__emulator/rules",
            TestRequests.Json("""{"printerId":"ready","target":"WebSocket","effect":"StaleNotifications","rpcMethod":"printer.objects.query","staleSeconds":30,"repeating":false,"remainingUses":1}"""));

        // Trigger the stale-notifications effect via a query call on this same connection.
        await socket.SendAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"printer.objects.query","params":{"objects":{"print_stats":null}},"id":2}"""),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
        _ = await ReceiveAsync(socket);

        using HttpResponseMessage gcode = await client.PostAsync(
            "/printer/gcode/script",
            TestRequests.Json("""{"script":"M117 silenced"}"""));
        gcode.EnsureSuccessStatusCode();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        Func<Task> receive = async () => await ReceiveAsync(socket, cts.Token);
        await receive.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task<JsonDocument> ReceiveAsync(WebSocket socket, CancellationToken ct = default)
    {
        byte[] buffer = new byte[32 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(stream.ToArray());
    }
}
