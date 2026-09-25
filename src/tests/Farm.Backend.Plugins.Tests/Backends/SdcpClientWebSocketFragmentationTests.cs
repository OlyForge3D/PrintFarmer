using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.Sdcp;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class SdcpClientWebSocketFragmentationTests
{
    [Fact]
    public async Task GetStatusAsync_WhenStatusResponseIsFragmentedAcrossFrames_ParsesStateCorrectly()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });

        builder.ListenOnEphemeralLoopbackPort();

        await using WebApplication app = builder.Build();

        app.UseWebSockets();

        app.Map("/websocket", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using WebSocket ws = await context.WebSockets.AcceptWebSocketAsync();

            // Read the client's request (ignored) so the client send completes.
            byte[] requestBuffer = new byte[4096];
            await ws.ReceiveAsync(requestBuffer, context.RequestAborted);

            string payload = JsonSerializer.Serialize(new
            {
                Status = new
                {
                    PrintInfo = new
                    {
                        Status = 13,
                        Progress = 50,
                        Filename = new string('a', 9000)
                    }
                },
                MainboardID = "test",
                TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Topic = string.Empty
            });

            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

            // Send the JSON response split across multiple frames.
            int firstChunkSize = Math.Min(100, payloadBytes.Length);
            await ws.SendAsync(payloadBytes.AsMemory(0, firstChunkSize), WebSocketMessageType.Text, endOfMessage: false, context.RequestAborted);
            await ws.SendAsync(payloadBytes.AsMemory(firstChunkSize), WebSocketMessageType.Text, endOfMessage: true, context.RequestAborted);

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
        });

        await app.StartAsync();

        string baseUrl = app.GetLoopbackBaseUrl();

        var logger = new Mock<ILogger<SdcpClient>>(MockBehavior.Loose);
        using var httpClient = new HttpClient();
        var client = new SdcpClient(httpClient, logger.Object, new Farm.Infrastructure.Settings.BackendTimeoutSettings());

        var status = await client.GetStatusAsync(baseUrl);

        status.IsOnline.Should().BeTrue();
        status.State.Should().Be("printing");

        await app.StopAsync();
    }

}
