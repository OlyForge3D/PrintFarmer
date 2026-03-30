using Farm.Backend.Plugin.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Backend.Plugin.FlashForge;

/// <summary>
/// Plugin descriptor for FlashForge backend client support.
/// FlashForge printers use a proprietary TCP serial protocol with G-code-like commands
/// on a configurable port (default 8899, some models like AD5X use 8080).
/// </summary>
public class FlashForgeBackendPlugin : IExtendedBackendPlugin
{
    /// <inheritdoc />
    public string BackendType => "flashforge";

    /// <inheritdoc />
    public string DisplayName => "FlashForge";

    /// <inheritdoc />
    public string Description => "Plugin for FlashForge 3D printers using proprietary TCP serial protocol";

    /// <inheritdoc />
    public Type ClientType => typeof(FlashForgeClient);

    /// <inheritdoc />
    public Type ClientInterfaceType => typeof(IFlashForgeClient);

    /// <inheritdoc />
    public Type? StatusClientType => null; // No dedicated status client yet; polling may be added later

    /// <inheritdoc />
    public Type? StatusClientInterfaceType => null;

    /// <inheritdoc />
    public Version Version => new(1, 0, 0);

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services)
    {
        // Services registered via RegisterAdditionalServices
    }

    /// <inheritdoc />
    public void RegisterAdditionalServices(IServiceCollection services)
    {
        // Register the FlashForge client. Each request gets a fresh instance since
        // the TCP connections are opened/closed per-command (no persistent connection).
        services.AddScoped<IFlashForgeClient>(provider =>
        {
            ILogger<FlashForgeClient> logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger<FlashForgeClient>();
            var timeouts = provider.GetRequiredService<IOptions<Farm.Infrastructure.Settings.BackendTimeoutSettings>>().Value;
            return new FlashForgeClient(logger, timeouts);
        });

        // Register the FlashForgePollingService hosted service
        // This service polls FlashForge printers for status updates every 10 seconds
        services.AddSingleton<IHostedService, FlashForgePollingService>();
    }

    /// <inheritdoc />
    public IEnumerable<string> GetConfigurationSections()
    {
        return ["FlashForge"];
    }
}
