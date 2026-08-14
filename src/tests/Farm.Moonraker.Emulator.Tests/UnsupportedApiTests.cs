using System.Net;
using FluentAssertions;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

/// <summary>
/// Confirms every explicitly out-of-scope Moonraker capability fails loudly (404) instead
/// of silently succeeding. The emulator simply never maps these routes, so ASP.NET
/// Core's default "no route matched" fallback is the failure mode under test.
/// </summary>
public sealed class UnsupportedApiTests : IClassFixture<DefaultDisabledControlApiFactory>
{
    private readonly DefaultDisabledControlApiFactory _factory;

    public UnsupportedApiTests(DefaultDisabledControlApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("GET", "/server/mqtt/publish")]
    [InlineData("POST", "/server/mqtt/publish")]
    [InlineData("POST", "/server/mqtt/subscribe")]
    [InlineData("GET", "/machine/peripherals/canbus")]
    [InlineData("GET", "/server/database/list")]
    [InlineData("GET", "/server/database/item")]
    [InlineData("POST", "/server/database/item")]
    [InlineData("DELETE", "/server/database/item")]
    [InlineData("GET", "/access/users/list")]
    [InlineData("POST", "/access/user")]
    [InlineData("DELETE", "/access/user")]
    [InlineData("POST", "/access/login")]
    [InlineData("GET", "/server/announcements/list")]
    [InlineData("POST", "/server/announcements/dismiss")]
    [InlineData("POST", "/machine/sudo/password")]
    [InlineData("GET", "/machine/peripherals/usb")]
    [InlineData("GET", "/machine/peripherals/serial")]
    [InlineData("GET", "/api/version")]
    [InlineData("GET", "/api/job")]
    [InlineData("POST", "/machine/update/client")]
    [InlineData("POST", "/machine/update/full")]
    [InlineData("POST", "/machine/update/recover")]
    [InlineData("GET", "/machine/device_power/devices")]
    [InlineData("GET", "/machine/device_power/device?device=printer")]
    [InlineData("POST", "/machine/device_power/device")]
    public async Task UnsupportedCapability_Returns404NotSilentSuccess(string method, string path)
    {
        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
