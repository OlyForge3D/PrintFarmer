using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Marker for backend clients whose status pipeline publishes backend-neutral
/// <see cref="PrinterSensorDto"/> entries. Backends that do not implement it report an empty
/// <c>sensors</c> list and advertise <c>supportsSensorTelemetry = false</c>.
/// Implementers must assign their complete current sensor snapshot to <c>Sensors</c> on every
/// cached status and <c>PrinterStatusUpdate</c> they broadcast (an assigned empty list clears
/// the cached sensors; leaving it unassigned preserves them), and may publish sensor-only
/// changes between status updates through <c>IPrinterStatusCacheWriter.UpdateSensors</c> and
/// <c>IPrinterStatusBroadcaster.BroadcastSensorChangeAsync</c>.
/// </summary>
#pragma warning disable CA1040 // Marker interface intentionally empty
public interface ISupportsSensorTelemetry
{
}
#pragma warning restore CA1040

/// <summary>
/// Opt-in targeted sensor/fan control. The server validates every request against the
/// sensor's advertised <see cref="PrinterSensorDto.Writable"/> descriptors before calling
/// this method, so implementations only receive known, available, writable sensors and
/// in-range values.
/// </summary>
public interface ISupportsSensorControl : ISupportsSensorTelemetry
{
    /// <summary>Sets <paramref name="quantity"/> of sensor <paramref name="sensorId"/> to <paramref name="value"/>.</summary>
    /// <returns>True when the backend accepted the command.</returns>
    Task<bool> SetSensorValueAsync(
        string baseUrl,
        string sensorId,
        PrinterSensorQuantity quantity,
        double value,
        PrinterCredential? credential,
        CancellationToken ct = default);
}
