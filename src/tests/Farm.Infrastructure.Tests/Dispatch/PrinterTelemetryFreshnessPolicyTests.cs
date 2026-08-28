using Farm.Backend.Plugin.Core;
using Farm.Backend.Plugin.FlashForge;
using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Backend.Plugin.Sdcp;
using Farm.Backend.Plugin.TestEmulator;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Dispatch;

public sealed class PrinterTelemetryFreshnessPolicyTests
{
    [Fact]
    public void ProviderAdvertisements_DriveBackendSpecificFreshnessSlas()
    {
        var registry = new BackendPluginRegistry();
        IExtendedBackendPlugin[] plugins =
        [
            new MoonrakerBackendPlugin(),
            new PrusaLinkBackendPlugin(),
            new SdcpBackendPlugin(),
            new OctoPrintBackendPlugin(),
            new FlashForgeBackendPlugin(),
            new TestEmulatorBackendPlugin(),
        ];
        foreach (IExtendedBackendPlugin plugin in plugins)
        {
            registry.Register(plugin);
            plugin.TelemetryCadence.Should().NotBeNull();
            plugin.TelemetryCadence!.MaximumObservationAge.Should()
                .BeGreaterThan(plugin.TelemetryCadence.ExpectedUpdateInterval);
        }

        var policy = new PrinterTelemetryFreshnessPolicy(registry);

        AssertSla(policy, 1, TimeSpan.FromSeconds(60));
        AssertSla(policy, 2, TimeSpan.FromSeconds(30));
        AssertSla(policy, 3, TimeSpan.FromSeconds(30));
        AssertSla(policy, 4, TimeSpan.FromSeconds(30));
        AssertSla(policy, 5, TimeSpan.FromSeconds(60));
        AssertSla(policy, 100, TimeSpan.FromSeconds(10));
        policy.TryGetMaximumObservationAge(999, out _).Should().BeFalse(
            "an unadvertised backend must fail physical safety gates closed");
    }

    private static void AssertSla(
        PrinterTelemetryFreshnessPolicy policy,
        int backendId,
        TimeSpan expected)
    {
        policy.TryGetMaximumObservationAge(backendId, out TimeSpan actual)
            .Should().BeTrue();
        actual.Should().Be(expected);
    }
}
