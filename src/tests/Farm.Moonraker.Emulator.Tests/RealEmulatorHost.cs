using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

/// <summary>
/// Hosts a real, network-listening emulator instance (genuine Kestrel bound to an
/// OS-assigned loopback port), as opposed to the in-memory <c>TestServer</c> transport
/// <see cref="EmulatorFactory"/> uses. The real <c>Farm.Backend.Plugin.Moonraker</c>
/// client and subscription service speak actual HTTP and raw <see cref="System.Net.WebSockets.ClientWebSocket"/>
/// connections — neither can be redirected through an in-memory <c>HttpMessageHandler</c>,
/// so exercising them against the emulator requires a real socket to connect to.
/// </summary>
public sealed class RealEmulatorHost : IAsyncLifetime
{
    private WebApplication? _app;

    /// <summary>The emulator's actual bound base address, e.g. "http://127.0.0.1:54321".</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>An <see cref="HttpClient"/> for talking to this instance's <c>/__emulator/**</c> control API.</summary>
    public HttpClient ControlClient { get; private set; } = new();

    public async Task InitializeAsync()
    {
        _app = Program.BuildApp(
        [
            "--urls=http://127.0.0.1:0",
            "--Emulator:Scenario=Ready",
            "--Emulator:PrinterId=real-ready",
            "--Emulator:PrinterName=moonraker-real-ready",
            "--Emulator:EnableControlApi=true",
        ]);
        await _app.StartAsync();

        IServerAddressesFeature addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not report a bound server address.");
        BaseUrl = addresses.Addresses.First();
        ControlClient = new HttpClient { BaseAddress = new Uri(BaseUrl) };
    }

    /// <summary>Resets the emulated printer back to its initial "ready" scenario between tests.</summary>
    public async Task ResetAsync()
    {
        using HttpResponseMessage response = await ControlClient.PostAsync("/__emulator/printer/reset", content: null);
        response.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        ControlClient.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
