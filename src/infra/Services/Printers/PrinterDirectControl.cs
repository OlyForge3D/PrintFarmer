namespace Farm.Infrastructure.Services.Printers;

/// <summary>Bounds ordinary manual commands without retaining operation receipts.</summary>
public static class PrinterDirectControl
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    public static bool IsManualMotion([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? operation) =>
        operation is "home" or "home_xy" or "home_z" or "move" or "move_to";

    /// <summary>
    /// Absolute setpoint writes whose unknown idle barriers can be reclaimed without replay.
    /// </summary>
    public static bool IsSetpoint([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? operation) =>
        operation is "set_temperature" or "set_fan_speed" or "set_speed_factor" or "set_flow_factor";

    /// <summary>Operator commands whose uncertain response must not become a persistent manual lockout.</summary>
    public static bool IsInteractive(string? operation) =>
        IsManualMotion(operation) || IsSetpoint(operation) ||
        operation is "adjust_z_offset" or "set_sensor_value" or "turn_off_heaters" or
            "disable_motors" or "extrude_filament" or "filament_load" or "filament_unload" or "filament_change" ||
        operation?.StartsWith("mmu_", StringComparison.Ordinal) == true;
}
