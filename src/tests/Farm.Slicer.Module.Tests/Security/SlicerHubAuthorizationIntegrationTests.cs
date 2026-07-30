using System.Net;
using FluentAssertions;

namespace Farm.Slicer.Module.Tests.Security;

public sealed class SlicerHubAuthorizationIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("/hubs/slicer-registry/negotiate?negotiateVersion=1")]
    [InlineData("/hubs/slicers/negotiate?negotiateVersion=1")]
    public async Task NegotiateAsync_WithoutAuthentication_IsDenied(string route)
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync(route, content: null);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
