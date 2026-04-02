using Farm.Backend.Plugin.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Backend.Plugin.TestEmulator;

/// <summary>
/// Plugin descriptor for the TestEmulator backend.
/// Simulates printer behavior for Playwright E2E testing without real hardware.
/// All services are conditionally registered — disabled by default.
/// </summary>
public class TestEmulatorBackendPlugin : IExtendedBackendPlugin
{
    public string BackendType => "testemulator";

    public string DisplayName => "Test Emulator";

    public string Description => "Fake backend plugin for Playwright E2E testing. Simulates printer behavior without real hardware.";

    public Type ClientType => typeof(TestEmulatorClient);

    public Type ClientInterfaceType => typeof(ITestEmulatorClient);

    public Type? StatusClientType => null;

    public Type? StatusClientInterfaceType => null;

    public Version Version => new(1, 0, 0);

    public void RegisterServices(IServiceCollection services)
    {
        // Extended plugin — services registered in RegisterAdditionalServices
    }

    public void RegisterAdditionalServices(IServiceCollection services)
    {
        // Resolve configuration to check if emulator is enabled.
        // At DI registration time we don't have a built provider yet, so we read
        // from the IConfiguration singleton that was already added to services.
        using ServiceProvider tempProvider = services.BuildServiceProvider();
        IConfiguration? config = tempProvider.GetService<IConfiguration>();
        bool enabled = config?.GetValue<bool>("TestEmulator:Enabled") ?? false;

        // Always register settings binding so IOptions<TestEmulatorSettings> resolves
        services.Configure<TestEmulatorSettings>(cfg =>
            config?.GetSection(TestEmulatorSettings.SectionName).Bind(cfg));

        // Always register the state manager (singleton) — harmless when empty
        services.AddSingleton<TestEmulatorStateManager>();

        // Always register the client (scoped) so plugin registry can resolve it
        services.AddScoped<ITestEmulatorClient, TestEmulatorClient>();

        if (!enabled)
        {
            return;
        }

        // Register the seeder and polling service only when enabled
        services.AddSingleton<IHostedService, TestEmulatorSeeder>();
        services.AddSingleton<IHostedService, TestEmulatorPollingService>();

        bool mockDiscovery = config?.GetValue<bool>("TestEmulator:MockDiscovery") ?? false;
        if (mockDiscovery)
        {
            services.AddSingleton<TestDiscoveryOverride>();
        }

        bool mockSpoolman = config?.GetValue<bool>("TestEmulator:MockSpoolman") ?? false;
        if (mockSpoolman)
        {
            // TestSpoolmanDataProvider is a static class — no DI registration needed.
            // Controllers/services access its data directly via static properties.
        }
    }

    public IEnumerable<string> GetConfigurationSections() => [TestEmulatorSettings.SectionName];
}
