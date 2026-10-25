namespace Farm.Infrastructure.Services.Printers;

/// <summary>Bounds ordinary manual commands without retaining operation receipts.</summary>
public static class PrinterDirectControl
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    public static bool IsManualMotion([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? operation) =>
        operation is "home" or "home_xy" or "home_z" or "move" or "move_to";

    /// <summary>
    /// Absolute setpoint writes (heater targets, fan speed, runtime factors). Re-issuing a
    /// setpoint supersedes an unknown earlier one, so an unknown outcome must not fence the printer.
    /// </summary>
    public static bool IsSetpoint([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? operation) =>
        operation is "set_temperature" or "set_fan_speed" or "set_speed_factor" or "set_flow_factor";

    /// <summary>Idle direct operations whose barrier is released (not retained) on an unknown outcome.</summary>
    public static bool ReleasesBarrierOnUnknown([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? operation) =>
        IsManualMotion(operation) || IsSetpoint(operation);
}
