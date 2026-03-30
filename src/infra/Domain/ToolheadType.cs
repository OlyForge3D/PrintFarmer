namespace Farm.Infrastructure.Domain;

/// <summary>
/// Classifies how a toolhead maps to printer hardware.
/// Toolchanger printers (Prusa XL, Snapmaker J1) use <see cref="Physical"/> toolheads.
/// MMU/AMS printers (Prusa MMU3, Bambu AMS) use <see cref="MmuGate"/> virtual toolheads.
/// Both share the same T0/T1/T2/T3 gcode addressing scheme.
/// </summary>
public enum ToolheadType
{
    /// <summary>
    /// A discrete physical toolhead with its own hotend and extruder (e.g., Prusa XL tool dock, Snapmaker J1 dual head).
    /// </summary>
    Physical = 0,

    /// <summary>
    /// A virtual gate on a multi-material unit that feeds filament through a shared hotend
    /// (e.g., Prusa MMU3 gate, Bambu AMS slot).
    /// </summary>
    MmuGate = 1
}
