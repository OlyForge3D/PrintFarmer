using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Backends;

/// <summary>
/// Behavior-level tests verifying that <see cref="SdcpClient.StartPrintAsync"/> correctly
/// propagates a rejected start as <see cref="PrinterBackendBusyException"/> when the
/// printer's CurrentStatus reports an active print job (#317).
///
/// Each test spins up a real Kestrel WebSocket server that replays pre-queued responses
/// to simulate the two-round-trip flow: StartPrint Ack=1 (rejection) → GetStatus
/// CurrentStatus (printing or idle).
/// </summary>
public sealed class SdcpClientBusyTests
{
    /// <summary>
    /// When the firmware rejects StartPrint (Ack=1) and CurrentStatus reports printing (code 1),
    /// <see cref="SdcpClient.StartPrintAsync"/> must throw <see cref="PrinterBackendBusyException"/>.
    /// </summary>
    [Fact]
    public async Task StartPrintAsync_WhenStartRejectedAndStatusIsPrinting_ThrowsPrinterBackendBusyException()
    {
        // Connection 1 (StartPrint, Cmd 128): firmware rejects with Ack=1.
        // Connection 2 (GetStatus, Cmd 0): printer reports CurrentStatus=[1] (actively printing).
        var responseQueue = new ConcurrentQueue<string>([
            BuildCommandAckResponse(cmd: 128, ack: 1),
            BuildStatusBroadcast(currentStatus: [1])
        ]);

        await using var env = await CreateSdcpMultiResponseServer(responseQueue);

        Func<Task> act = () => env.Client.StartPrintAsync(env.BaseUrl, "test.gcode");

        await act.Should().ThrowAsync<PrinterBackendBusyException>(
            because: "rejected start with CurrentStatus=printing must propagate as PrinterBackendBusyException (#317)");
    }

    /// <summary>
    /// When the firmware rejects StartPrint (Ack=1) and CurrentStatus reports "starting" (code 9),
    /// <see cref="SdcpClient.StartPrintAsync"/> must also throw <see cref="PrinterBackendBusyException"/>.
    /// Code 9 is a transient "starting" state included in the busy set.
    /// </summary>
    [Fact]
    public async Task StartPrintAsync_WhenStartRejectedAndStatusIsStarting_ThrowsPrinterBackendBusyException()
    {
        var responseQueue = new ConcurrentQueue<string>([
            BuildCommandAckResponse(cmd: 128, ack: 1),
            BuildStatusBroadcast(currentStatus: [9])
        ]);

        await using var env = await CreateSdcpMultiResponseServer(responseQueue);

        Func<Task> act = () => env.Client.StartPrintAsync(env.BaseUrl, "test.gcode");

        await act.Should().ThrowAsync<PrinterBackendBusyException>();
    }

    /// <summary>
    /// When the firmware rejects StartPrint (Ack=1) but CurrentStatus reports idle (code 0),
    /// <see cref="SdcpClient.StartPrintAsync"/> must return false rather than throw.
    /// This is the negative case — rejection with a non-busy reason.
    /// </summary>
    [Fact]
    public async Task StartPrintAsync_WhenStartRejectedAndStatusIsIdle_ReturnsFalseWithoutException()
    {
        // Connection 1: Ack=1 (rejected). Connection 2: CurrentStatus=[0] (idle/unknown error).
        var responseQueue = new ConcurrentQueue<string>([
            BuildCommandAckResponse(cmd: 128, ack: 1),
            BuildStatusBroadcast(currentStatus: [0])
        ]);

        await using var env = await CreateSdcpMultiResponseServer(responseQueue);

        bool result = await env.Client.StartPrintAsync(env.BaseUrl, "test.gcode");

        result.Should().BeFalse(
            because: "a non-busy rejection should return false, not throw PrinterBackendBusyException");
    }

    // ==================== Helper Methods ====================

    /// <summary>
    /// Creates a Kestrel WebSocket server that serves pre-queued response payloads,
    /// one per WebSocket connection, in FIFO order.
    /// </summary>
    private static async Task<SdcpTestEnvironment> CreateSdcpMultiResponseServer(
        ConcurrentQueue<string> responseQueue)
    {
        int port = GetFreeTcpPort();

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

        WebApplication app = builder.Build();
        app.UseWebSockets();

        app.Map("/websocket", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using WebSocket ws = await context.WebSockets.AcceptWebSocketAsync();

            // Read the client's request (discard — we respond from the queue).
            byte[] requestBuffer = new byte[8192];
            await ws.ReceiveAsync(requestBuffer, context.RequestAborted);

            if (responseQueue.TryDequeue(out string? payload))
            {
                byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
                await ws.SendAsync(payloadBytes, WebSocketMessageType.Text, true, context.RequestAborted);
            }

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
        });

        await app.StartAsync();

        string baseUrl = $"http://127.0.0.1:{port}";
        var logger = new Mock<ILogger<SdcpClient>>(MockBehavior.Loose);
        using var httpClient = new System.Net.Http.HttpClient();
        var client = new SdcpClient(httpClient, logger.Object, new BackendTimeoutSettings());

        return new SdcpTestEnvironment(app, client, baseUrl);
    }

    private static string BuildCommandAckResponse(int cmd, int ack) =>
        JsonSerializer.Serialize(new
        {
            Id = (string?)null,
            Data = new
            {
                Cmd = cmd,
                Data = new { Ack = ack },
                RequestID = "req-ack",
                MainboardID = "mb-test",
                TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            Topic = (string?)null
        });

    private static string BuildStatusBroadcast(int[] currentStatus) =>
        JsonSerializer.Serialize(new
        {
            Status = new { CurrentStatus = currentStatus },
            MainboardID = "mb-test",
            TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Topic = string.Empty
        });

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class SdcpTestEnvironment(WebApplication app, SdcpClient client, string baseUrl)
        : IAsyncDisposable
    {
        public SdcpClient Client { get; } = client;
        public string BaseUrl { get; } = baseUrl;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
