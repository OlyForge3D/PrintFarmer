using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
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

    [Fact]
    public async Task ListenOnEphemeralLoopbackPort_IgnoresAmbientKestrelEndpoints()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Ambient:Url"] = "http://127.0.0.1:0",
        });
        builder.ListenOnEphemeralLoopbackPort();
        await using WebApplication app = builder.Build();
        await app.StartAsync();

        ICollection<string> addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;

        addresses.Should().ContainSingle("configured Kestrel endpoints are additive and must be suppressed")
            .Which.Should().Be(app.GetLoopbackBaseUrl());

        await app.StopAsync();
    }

    [Fact]
    public async Task StartOnLoopbackAsync_WhenEnvironmentConstructionThrows_StopsAndDisposesApp()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.ListenOnEphemeralLoopbackPort();
        WebApplication app = builder.Build();
        IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

        Func<Task> act = () => app.StartOnLoopbackAsync<object>(_ => throw new InvalidOperationException("boom"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        lifetime.ApplicationStopped.IsCancellationRequested.Should().BeTrue();
        FluentActions.Invoking(() => app.Services.GetRequiredService<IServer>())
            .Should().Throw<ObjectDisposedException>();
    }
}
