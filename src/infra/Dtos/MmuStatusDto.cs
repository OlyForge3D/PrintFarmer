namespace Farm.Infrastructure;

/// <summary>
/// Represents the status of a Multi-Material Unit (MMU/ERCF/AMS/Qidibox/AFC) or U1 physical lane set attached to a printer.
/// Supports Happy Hare MMU, Qidibox filament box, AFC (BoxTurtle/NightOwl/QuattroBox), and Snapmaker U1 protocols via Moonraker.
/// </summary>
/// <param name="Enabled">Whether the MMU is detected and enabled.</param>
/// <param name="IsHomed">Whether the MMU has been homed.</param>
/// <param name="ActiveTool">Currently selected tool index (-1 = none, -2 = unknown).</param>
/// <param name="ActiveGate">Currently selected gate index.</param>
/// <param name="FilamentState">Filament load state: "Loaded", "Unloaded", or "Unknown".</param>
/// <param name="Action">Current action: "Idle", "Loading", "Unloading", "Forming Tip", etc.</param>
/// <param name="NumGates">Total number of gates/slots available.</param>
/// <param name="HasBypass">Whether the MMU has a bypass (direct-drive override).</param>
/// <param name="EndlessSpool">Whether endless spool mode is active.</param>
/// <param name="ClogDetection">Whether clog detection is active.</param>
/// <param name="Gates">Per-gate slot information.</param>
/// <param name="MmuType">The MMU protocol type: "HappyHare", "Qidibox", "AFC", or "SnapmakerU1".</param>
public record MmuStatusDto(
    bool Enabled,
    bool IsHomed,
    int ActiveTool,
    int ActiveGate,
    string? FilamentState,
    string? Action,
    int NumGates,
    bool HasBypass,
    bool EndlessSpool,
    bool ClogDetection,
    MmuGateDto[] Gates,
    string MmuType = MmuProtocol.Unknown);

/// <summary>
/// Status of a single gate/slot on the MMU.
/// </summary>
/// <param name="Index">Gate index (0-based).</param>
/// <param name="Status">Gate status: 0 = empty, 1 = available, 2 = unknown, -1 = disabled.</param>
/// <param name="Material">Material type loaded in this gate (e.g., "PLA", "PETG", "ASA").</param>
/// <param name="Color">CSS color string for the filament (e.g., "#FF0000", "red").</param>
/// <param name="FilamentName">Filament brand/name (e.g., "eSun PLA+").</param>
/// <param name="SpoolId">Spoolman spool ID associated with this gate (-1 = none).</param>
/// <param name="Name">Lane/slot name (e.g., "lane1" for AFC), or null if not applicable.</param>
public record MmuGateDto(
    int Index,
    int Status,
    string? Material,
    string? Color,
    string? FilamentName,
    int SpoolId,
    string? Name = null);
