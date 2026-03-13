namespace Farm.Infrastructure;

// ===========================================================================
// Printer SignalR Real-Time Update DTOs
// ===========================================================================
// Records used for real-time printer status updates via SignalR.
// These are lightweight DTOs optimized for frequent transmission.
// ===========================================================================

/// <summary>
/// SignalR event payload for extruder/hotend temperature updates.
/// </summary>
/// <param name="PrinterId">Unique identifier of the printer.</param>
/// <param name="Temperature">Current extruder temperature in Celsius, or null if unavailable.</param>
/// <param name="Target">Target extruder temperature in Celsius, or null if not heating.</param>
public record PrinterExtruderUpdate(
    Guid PrinterId,
    double? Temperature,
    double? Target);

/// <summary>
/// SignalR event payload for heated bed temperature updates.
/// </summary>
/// <param name="PrinterId">Unique identifier of the printer.</param>
/// <param name="Temperature">Current bed temperature in Celsius, or null if unavailable.</param>
/// <param name="Target">Target bed temperature in Celsius, or null if not heating.</param>
public record PrinterHeaterBedUpdate(
    Guid PrinterId,
    double? Temperature,
    double? Target);

/// <summary>
/// SignalR event payload for print state and progress updates.
/// </summary>
/// <param name="PrinterId">Unique identifier of the printer.</param>
/// <param name="State">Current printer state (e.g., "printing", "idle", "paused").</param>
/// <param name="Progress">Print progress as percentage (0-100), or null if not printing.</param>
/// <param name="JobName">Name of the current print job, or null if not printing.</param>
/// <param name="FileName">Original filename of the G-code file being printed, or null if not printing.</param>
public record PrinterStateUpdate(
    Guid PrinterId,
    string? State,
    double? Progress,
    string? JobName,
    string? FileName = null);

/// <summary>
/// SignalR event payload for toolhead position and homing status updates.
/// </summary>
/// <param name="PrinterId">Unique identifier of the printer.</param>
/// <param name="X">Current X position in mm, or null if unknown.</param>
/// <param name="Y">Current Y position in mm, or null if unknown.</param>
/// <param name="Z">Current Z position in mm, or null if unknown.</param>
/// <param name="HomedAxes">String indicating homed axes (e.g., "xyz", "xy"), or null.</param>
public record PrinterToolheadUpdate(
    Guid PrinterId,
    double? X,
    double? Y,
    double? Z,
    string? HomedAxes);

/// <summary>
/// Temperature target values for hotend and heated bed.
/// Used in printer control commands.
/// </summary>
/// <param name="Hotend">Target hotend temperature in Celsius, or null to leave unchanged.</param>
/// <param name="Bed">Target bed temperature in Celsius, or null to leave unchanged.</param>
public record TempTargets(double? Hotend, double? Bed);

/// <summary>
/// Command to move the printer toolhead to specified coordinates.
/// Used for manual positioning via the UI.
/// </summary>
/// <param name="X">Target X position in mm, or null to leave unchanged.</param>
/// <param name="Y">Target Y position in mm, or null to leave unchanged.</param>
/// <param name="Z">Target Z position in mm, or null to leave unchanged.</param>
/// <param name="F">Feedrate in mm/min, or null for default speed.</param>
public record MoveRequest(double? X, double? Y, double? Z, double? F);
