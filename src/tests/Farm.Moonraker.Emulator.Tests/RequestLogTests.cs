using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Moonraker.Emulator.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

public sealed class RequestLogTests
{
    [Theory]
    [InlineData("GET", "/printer/info", false)]
    [InlineData("HEAD", "/server/info", false)]
    [InlineData("OPTIONS", "/printer/info", false)]
    [InlineData("POST", "/websocket", false)]
    [InlineData("POST", "/printer/gcode/script", true)]
    [InlineData("post", "/printer/emergency_stop", true)]
    [InlineData("DELETE", "/server/files/gcodes/benchy.gcode", true)]
    [InlineData("PUT", "/printer/info", true)]
    public void IsHttpCommand_ClassifiesConservatively(string method, string path, bool expected) =>
        RequestLog.IsHttpCommand(method, path).Should().Be(expected);

    [Theory]
    [InlineData("GET", false)]
    [InlineData("get", false)]
    [InlineData("POST", true)]
    [InlineData("DELETE", true)]
    [InlineData(null, true)]
    public void IsHttpCommand_ClassifiesSpoolmanProxyByForwardedMethod(string? proxiedMethod, bool expected) =>
        RequestLog.IsHttpCommand("POST", RequestLog.SpoolmanProxyPath, proxiedMethod).Should().Be(expected);

    [Fact]
    public void IsHttpCommand_IgnoresForwardedMethodOutsideSpoolmanProxy() =>
        RequestLog.IsHttpCommand("POST", "/printer/gcode/script", "GET").Should().BeTrue();

    [Theory]
    [InlineData("server.info", false)]
    [InlineData("printer.objects.subscribe", false)]
    [InlineData("server.files.get_directory", false)]
    [InlineData("printer.gcode.script", true)]
    [InlineData("printer.emergency_stop", true)]
    [InlineData("", true)]
    public void IsRpcCommand_OnlyKnownReadsAreNotCommands(string method, bool expected) =>
        RequestLog.IsRpcCommand(method).Should().Be(expected);

    [Fact]
    public void Record_KeepsCumulativeCountersBeyondRetainedCapacity()
    {
        RequestLog log = new();
        for (int i = 0; i < RequestLog.Capacity + 5; i++)
        {
            log.RecordHttp("GET", "/printer/info");
        }

        log.RecordHttp("POST", "/printer/gcode/script");

        RequestLogSnapshot snapshot = log.Snapshot();
        snapshot.Total.Should().Be(RequestLog.Capacity + 6);
        snapshot.Commands.Should().Be(1);
        snapshot.Entries.Should().HaveCount(RequestLog.Capacity);
        snapshot.Entries[^1].Should().Match<RequestLogEntry>(e =>
            e.IsCommand && e.Method == "POST" && e.Target == "/printer/gcode/script" && e.Sequence == RequestLog.Capacity + 6);
    }

    [Fact]
    public void Retarget_UpdatesRetainedEntryWithoutChangingCounters()
    {
        RequestLog log = new();
        RequestLogEntry entry = log.RecordHttp("POST", "/server/files/upload");

        log.Retarget(entry.Sequence, "/server/files/upload:gcodes/benchy.gcode:print=true");

        RequestLogSnapshot snapshot = log.Snapshot();
        snapshot.Total.Should().Be(1);
        snapshot.Commands.Should().Be(1);
        snapshot.Entries.Should().ContainSingle().Which.Target.Should().Be("/server/files/upload:gcodes/benchy.gcode:print=true");
    }
}

public sealed class RequestLogEndpointTests : IClassFixture<ReadyPrinterFactory>
{
    private readonly ReadyPrinterFactory _factory;

    public RequestLogEndpointTests(ReadyPrinterFactory factory) => _factory = factory;

    [Fact]
    public async Task Requests_RecordsProtocolTrafficButNotControlApi_AndSurvivesReset()
    {
        using HttpClient client = _factory.CreateClient();
        JsonElement before = await GetRequestsAsync(client);
        long totalBefore = before.GetProperty("total").GetInt64();
        long commandsBefore = before.GetProperty("commands").GetInt64();

        (await client.GetAsync("/printer/info")).EnsureSuccessStatusCode();
        (await client.PostAsync("/printer/gcode/script", TestRequests.Json("""{"script":"M117 hi"}"""))).EnsureSuccessStatusCode();
        (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();
        (await client.PostAsync("/__emulator/reset", content: null)).EnsureSuccessStatusCode();

        JsonElement after = await GetRequestsAsync(client);
        after.GetProperty("total").GetInt64().Should().Be(totalBefore + 2);
        after.GetProperty("commands").GetInt64().Should().Be(commandsBefore + 1);

        JsonElement[] newEntries = [.. after.GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("sequence").GetInt64() > totalBefore)];
        newEntries.Select(e => (e.GetProperty("method").GetString(), e.GetProperty("target").GetString(), e.GetProperty("isCommand").GetBoolean()))
            .Should().Equal(("GET", "/printer/info", false), ("POST", "/printer/gcode/script", true));
    }

    [Fact]
    public async Task Requests_ClassifiesWebSocketRpcMethods()
    {
        using HttpClient client = _factory.CreateClient();
        long commandsBefore = (await GetRequestsAsync(client)).GetProperty("commands").GetInt64();

        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        using WebSocket socket = await wsClient.ConnectAsync(new Uri("ws://localhost/websocket"), CancellationToken.None);
        await SendAndReceiveAsync(socket, """{"jsonrpc":"2.0","method":"server.info","id":1}""");
        await SendAndReceiveAsync(socket, """{"jsonrpc":"2.0","method":"printer.gcode.script","params":{"script":"G28"},"id":2}""");

        JsonElement after = await GetRequestsAsync(client);
        after.GetProperty("commands").GetInt64().Should().Be(commandsBefore + 1);
        after.GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("transport").GetString() == "jsonrpc")
            .Select(e => (e.GetProperty("method").GetString(), e.GetProperty("isCommand").GetBoolean()))
            .Should().Contain([("server.info", false), ("printer.gcode.script", true)]);
    }

    [Fact]
    public async Task Requests_ClassifiesSpoolmanProxyReadsAndWrites_AndEndpointStillReadsBody()
    {
        using HttpClient client = _factory.CreateClient();
        long totalBefore = (await GetRequestsAsync(client)).GetProperty("total").GetInt64();

        using HttpResponseMessage read = await client.PostAsync(
            "/server/spoolman/proxy", TestRequests.Json("""{"request_method":"GET","path":"/v1/spool"}"""));
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        await client.PostAsync(
            "/server/spoolman/proxy", TestRequests.Json("""{"request_method":"PATCH","path":"/v1/spool/1"}"""));
        await client.PostAsync("/server/spoolman/proxy", TestRequests.Json("not json"));

        JsonElement after = await GetRequestsAsync(client);
        after.GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("sequence").GetInt64() > totalBefore)
            .Select(e => e.GetProperty("isCommand").GetBoolean())
            .Should().Equal(false, true, true);
    }

    [Fact]
    public async Task Requests_RetargetsUploadAndPrintStartWithFileNames()
    {
        using HttpClient client = _factory.CreateClient();
        long totalBefore = (await GetRequestsAsync(client)).GetProperty("total").GetInt64();
        const string filename = "queued-fixture.gcode";

        await TestRequests.EnsureGcodeFileExistsAsync(client, filename);
        (await client.PostAsync("/printer/print/start", TestRequests.Json($$"""{"filename":"{{filename}}"}"""))).EnsureSuccessStatusCode();

        JsonElement after = await GetRequestsAsync(client);
        after.GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("sequence").GetInt64() > totalBefore)
            .Select(e => e.GetProperty("target").GetString())
            .Should().Contain([
                $"/server/files/upload:gcodes/{filename}:print=false",
                $"/printer/print/start:{filename}",
            ]);
    }

    private static async Task<JsonElement> GetRequestsAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/__emulator/requests");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task SendAndReceiveAsync(WebSocket socket, string json)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        byte[] buffer = new byte[32 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        }
        while (!result.EndOfMessage);
    }
}
