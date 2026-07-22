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
        // Read configuration without building a temporary ServiceProvider.
        // In .NET 10 minimal hosting, IConfiguration may be registered as a factory
        // rather than an ImplementationInstance. Try ImplementationInstance first,
        // then fall back to building a temporary provider.
        IConfiguration? config = null;
        foreach (ServiceDescriptor sd in services)
        {
            if (sd.ServiceType == typeof(IConfiguration) && sd.ImplementationInstance is IConfiguration c)
            {
                config = c;
                break;
            }
        }

        // Fallback: build a minimal provider to resolve IConfiguration
        if (config is null)
        {
            using ServiceProvider tempProvider = services.BuildServiceProvider();
            config = tempProvider.GetService<IConfiguration>();
        }

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
            // TestEmulator is disabled — skip seeder/polling registration
            return;
        }

        // TestEmulator is enabled — register seeder and polling service

        // Register the seeder and polling service only when enabled
        services.AddSingleton<IHostedService, TestEmulatorSeeder>();
        services.AddSingleton<IHostedService, TestEmulatorPollingService>();

        bool mockDiscovery = config?.GetValue<bool>("TestEmulator:MockDiscovery") ?? false;
        if (mockDiscovery)
        {
            services.AddSingleton<TestDiscoveryOverride>();
        }

        // NOTE: TestSpoolmanDataProvider and TestDiscoveryOverride are registered but not yet
        // wired into the actual discovery/Spoolman pipelines. These are scaffolding for future
        // integration. See TODO comments in each class for the planned wiring approach.
    }

    public IEnumerable<string> GetConfigurationSections() => [TestEmulatorSettings.SectionName];
}
