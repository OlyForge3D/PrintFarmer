using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Farm.Moonraker.Emulator.Tests;

/// <summary>
/// Base <see cref="WebApplicationFactory{TEntryPoint}"/> that configures the emulator
/// exactly the way one Compose service instance would be configured in production: a
/// fixed <c>Emulator:Scenario</c>, <c>Emulator:PrinterId</c>, and
/// <c>Emulator:PrinterName</c>, with no per-request Host/path dispatch. Concrete
/// subclasses pick the scenario; most contract tests only need one running instance
/// and drive scenario/state changes through the control API from there, exactly like
/// <c>/__emulator/printer/scenario</c> would in a real deployment's validation stack.
/// </summary>
public abstract class EmulatorFactory : WebApplicationFactory<Program>
{
    protected abstract string Scenario { get; }

    protected virtual bool EnableControlApi => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        string printerId = Scenario.ToLowerInvariant();
        builder.UseSetting("Emulator:Scenario", Scenario);
        builder.UseSetting("Emulator:PrinterId", printerId);
        builder.UseSetting("Emulator:PrinterName", $"moonraker-{printerId}");
        builder.UseSetting("Emulator:EnableControlApi", EnableControlApi ? "true" : "false");

        if (!EnableControlApi)
        {
            // Forces the true production default (appsettings.Development.json turns
            // the control API on for developer convenience, which would otherwise mask
            // what "disabled by default" actually means).
            builder.UseEnvironment("Production");
        }
    }
}

/// <summary>A control-API-enabled instance seeded as the "ready" scenario — the default fixture for most contract tests.</summary>
public sealed class ReadyPrinterFactory : EmulatorFactory
{
    protected override string Scenario => "Ready";
}

/// <summary>A control-API-enabled instance seeded as the "printing" scenario.</summary>
public sealed class PrintingPrinterFactory : EmulatorFactory
{
    protected override string Scenario => "Printing";
}

/// <summary>A control-API-enabled instance seeded as the "paused" scenario.</summary>
public sealed class PausedPrinterFactory : EmulatorFactory
{
    protected override string Scenario => "Paused";
}

/// <summary>A control-API-enabled instance seeded as the "shutdown" scenario.</summary>
public sealed class ShutdownPrinterFactory : EmulatorFactory
{
    protected override string Scenario => "Shutdown";
}

/// <summary>
/// A "ready" scenario instance with the control API left at its production default
/// (disabled), used to verify <c>/__emulator/**</c> is unreachable unless explicitly
/// opted in, and to exercise the unsupported-capability 404 surface.
/// </summary>
public sealed class DefaultDisabledControlApiFactory : EmulatorFactory
{
    protected override string Scenario => "Ready";

    protected override bool EnableControlApi => false;
}
