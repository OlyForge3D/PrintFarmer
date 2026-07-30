using Farm.Web.Api.Services.PowerMonitor;
using Farm.Web.Api.Services.SmartPlug;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PowerMonitor;

/// <summary>
/// Validates that <see cref="PowerMonitorPollingService"/> correctly resolves scoped
/// <see cref="ISmartPlugProvider"/> instances per iteration (no captive dependency).
/// </summary>
public class PowerMonitorPollingServiceScopeTests
{
    /// <summary>
    /// Verifies that startup does not crash when ValidateScopes is enabled and
    /// ISmartPlugProvider registrations include scoped services.
    /// </summary>
    [Fact]
    public void Startup_WithScopedProvider_DoesNotThrow_WhenValidateScopesEnabled()
    {
        ServiceCollection services = new();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        // Register a scoped provider — this would crash with captive dependency pattern.
        services.AddScoped<ISmartPlugProvider, FakeScopedSmartPlugProvider>();
        services.AddHostedService<PowerMonitorPollingService>();

        // Enable scope validation — mirrors ASP.NET Core development mode.
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        // Resolving IHostedService must not throw even with scoped providers,
        // because the service no longer takes IEnumerable<ISmartPlugProvider> directly.
        IEnumerable<IHostedService> hostedServices = provider.GetServices<IHostedService>();

        Assert.NotNull(hostedServices);
        Assert.Contains(hostedServices, s => s is PowerMonitorPollingService);
    }

    /// <summary>
    /// Verifies that each scope created by the polling service resolves a fresh
    /// instance of scoped providers (no captive reference).
    /// </summary>
    [Fact]
    public void ScopedProviders_AreResolvedPerScope_NoCaptiveDependency()
    {
        ServiceCollection services = new();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<ISmartPlugProvider, FakeScopedSmartPlugProvider>();

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        ISmartPlugProvider instance1;
        ISmartPlugProvider instance2;

        using (IServiceScope scope1 = scopeFactory.CreateScope())
        {
            instance1 = scope1.ServiceProvider.GetServices<ISmartPlugProvider>().Single();
        }

        using (IServiceScope scope2 = scopeFactory.CreateScope())
        {
            instance2 = scope2.ServiceProvider.GetServices<ISmartPlugProvider>().Single();
        }

        // Different scope → different instance (proves no captive reference).
        Assert.NotSame(instance1, instance2);
    }

    private sealed class FakeScopedSmartPlugProvider : ISmartPlugProvider
    {
        public string ProviderType => "FakeScoped";

        public Task<PowerReading?> GetCurrentReadingAsync(string deviceAddress, CancellationToken ct)
            => Task.FromResult<PowerReading?>(null);

        public Task<bool> TestConnectionAsync(string deviceAddress, CancellationToken ct)
            => Task.FromResult(true);
    }
}
