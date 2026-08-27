using System.Net;
using System.Net.WebSockets;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

public sealed class AuthenticationTests : IClassFixture<ApiKeyProtectedFactory>
{
    private readonly ApiKeyProtectedFactory _factory;

    public AuthenticationTests(ApiKeyProtectedFactory factory) => _factory = factory;

    [Fact]
    public async Task ProtocolRoute_WithoutApiKey_ReturnsMoonrakerUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/info");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("WebRequestError");
    }

    [Fact]
    public async Task ProtocolRoute_WithApiKey_Succeeds()
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "moonraker-test-api-key");

        using HttpResponseMessage response = await client.GetAsync("/server/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WebSocketUpgrade_RequiresApiKey()
    {
        WebSocketClient client = _factory.Server.CreateWebSocketClient();
        Func<Task> connectWithoutKey = async () =>
        {
            using WebSocket socket = await client.ConnectAsync(new Uri("ws://localhost/websocket"), CancellationToken.None);
        };
        await connectWithoutKey.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*401*");

        WebSocketClient authenticated = _factory.Server.CreateWebSocketClient();
        authenticated.ConfigureRequest = request => request.Headers["X-Api-Key"] = "moonraker-test-api-key";
        using WebSocket connected = await authenticated.ConnectAsync(new Uri("ws://localhost/websocket"), CancellationToken.None);
        connected.State.Should().Be(WebSocketState.Open);
    }
}
