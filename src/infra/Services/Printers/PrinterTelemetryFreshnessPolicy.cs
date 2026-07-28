using System.Reflection;
using Farm.Backend.Plugin.Core;

namespace Farm.Infrastructure.Services.Printers;

public interface IPrinterTelemetryFreshnessPolicy
{
    bool TryGetMaximumObservationAge(
        int backendId,
        out TimeSpan maximumObservationAge);
}

public sealed class PrinterTelemetryFreshnessPolicy
    : IPrinterTelemetryFreshnessPolicy
{
    private readonly Dictionary<int, BackendTelemetryCadence> _cadences;

    public PrinterTelemetryFreshnessPolicy(IBackendPluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _cadences = registry
            .GetAllExtendedPlugins()
            .Select(plugin => new
            {
                Plugin = plugin,
                Attribute = plugin.ClientType.Assembly
                    .GetCustomAttribute<BackendPluginAttribute>(),
            })
            .Where(entry =>
                entry.Attribute is not null &&
                entry.Plugin.TelemetryCadence is not null &&
                entry.Plugin.TelemetryCadence.ExpectedUpdateInterval > TimeSpan.Zero &&
                entry.Plugin.TelemetryCadence.MaximumObservationAge >=
                    entry.Plugin.TelemetryCadence.ExpectedUpdateInterval)
            .ToDictionary(
                entry => entry.Attribute!.BackendId,
                entry => entry.Plugin.TelemetryCadence!);
    }

    public bool TryGetMaximumObservationAge(
        int backendId,
        out TimeSpan maximumObservationAge)
    {
        if (_cadences.TryGetValue(backendId, out BackendTelemetryCadence? cadence))
        {
            maximumObservationAge = cadence.MaximumObservationAge;
            return true;
        }

        maximumObservationAge = TimeSpan.Zero;
        return false;
    }
}
