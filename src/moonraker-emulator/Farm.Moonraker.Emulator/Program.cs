using Farm.Moonraker.Emulator;
using Farm.Moonraker.Emulator.Domain;
using Farm.Moonraker.Emulator.Endpoints;
using Farm.Moonraker.Emulator.Middleware;
using Farm.Moonraker.Emulator.Options;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

await Program.BuildApp(args).RunAsync();

/// <summary>Entry point marker so WebApplicationFactory&lt;Program&gt; can target this assembly in tests.</summary>
public sealed partial class Program
{
    private Program()
    {
    }

    /// <summary>
    /// Builds the fully configured emulator <see cref="WebApplication"/> without running
    /// it. Extracted from top-level <c>Main</c> so integration tests can host a real,
    /// network-listening instance (e.g. bound to an OS-assigned loopback port via
    /// <c>--urls=http://127.0.0.1:0</c>) for the real <c>Farm.Backend.Plugin.Moonraker</c>
    /// client/subscription code — which speaks genuine HTTP/WebSocket, not the in-memory
    /// <c>TestServer</c> transport — to connect to.
    /// </summary>
    public static WebApplication BuildApp(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.WebHost.ConfigureKestrel(kestrel => kestrel.AllowSynchronousIO = false);

        // Default Moonraker port, applied only when nothing else already configured a
        // listen URL (ASPNETCORE_URLS env var, --urls on the command line, or
        // Properties/launchSettings.json) — an explicit ask (e.g. an integration test
        // requesting an OS-assigned loopback port via --urls=http://127.0.0.1:0) must
        // win over this default rather than being silently clobbered by it.
        // Plain HTTP is intentional: real Moonraker instances speak plaintext HTTP/WS on
        // port 7125 (TLS is handled upstream, if at all, by a reverse proxy) so the emulator
        // must match that wire protocol exactly.
        if (string.IsNullOrEmpty(builder.Configuration["urls"]))
        {
#pragma warning disable S5332 // Using http protocol is insecure - intentional Moonraker protocol parity
            builder.WebHost.UseUrls("http://127.0.0.1:7125");
#pragma warning restore S5332
        }

        builder.Services.Configure<EmulatorOptions>(builder.Configuration.GetSection(EmulatorOptions.SectionName));
        builder.Services.AddSingleton<PrinterRegistry>();
        builder.Services.AddHostedService<VirtualTimeTickerService>();
        builder.Services.AddHealthChecks();

        WebApplication app = builder.Build();

        bool controlApiEnabled = app.Services.GetRequiredService<IOptions<EmulatorOptions>>().Value.EnableControlApi;
        if (controlApiEnabled)
        {
            app.Logger.LogWarning("Emulator:EnableControlApi=true — the /__emulator/** test-control surface is reachable.");
        }

        // Order matters: WebSockets support and printer resolution/fault-injection must run
        // before routing selects an endpoint, otherwise the path-prefix stripping performed by
        // PrinterResolutionMiddleware would happen too late to affect which route matches.
        app.UseWebSockets();
        app.UseMiddleware<PrinterResolutionMiddleware>();
        app.UseRouting();

        app.MapHealthChecks("/healthz", new HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = report.Status == HealthStatus.Healthy ? "ok" : "unhealthy",
                    timestamp = DateTime.UtcNow,
                }));
            },
        });

        app.MapMoonrakerRest();
        app.MapMoonrakerWebSocket();

        if (controlApiEnabled)
        {
            app.MapEmulatorControlApi();
        }

        app.MapFallback(context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(
                """{"error":"NotImplemented","message":"This Moonraker capability is not supported by the PrintFarmer emulator."}""");
        });

        return app;
    }
}
