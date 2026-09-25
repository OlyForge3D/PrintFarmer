using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class LoopbackKestrelTests
{
    [Fact]
    public async Task ListenOnEphemeralLoopbackPort_BindsOnlyIpv4LoopbackWithAssignedPort()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.ListenOnEphemeralLoopbackPort();
        await using WebApplication app = builder.Build();
        app.MapGet("/", () => "ok");
        await app.StartAsync();

        string baseUrl = app.GetLoopbackBaseUrl();
        ICollection<string> addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;

        addresses.Should().ContainSingle()
            .Which.Should().StartWith("http://127.0.0.1:", "no [::1] or localhost listener may be bound (#3025)");
        new Uri(baseUrl).Port.Should().BePositive();
        using HttpClient http = new();
        (await http.GetStringAsync(baseUrl)).Should().Be("ok");

        await app.StopAsync();
    }
}
