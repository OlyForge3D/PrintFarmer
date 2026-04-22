using Farm.Infrastructure;

namespace Farm.Backend.Plugin.Moonraker;

// Persistent state for a printer to avoid overwriting good values with nulls
internal sealed class PrinterState
{
    public double? X { get; set; }

    public double? Y { get; set; }

    public double? Z { get; set; }

    public double? HotendTemp { get; set; }

    public double? BedTemp { get; set; }

    public double? HotendTarget { get; set; }

    public double? BedTarget { get; set; }

    public string? State { get; set; }

    /// <summary>
    /// Tracks the previous state for detecting state transitions (e.g., printing → standby).
    /// </summary>
    public string? PreviousState { get; set; }

    public double? Progress { get; set; }

    public double? PrintDuration { get; set; }

    public string? JobName { get; set; }

    public string? HomedAxes { get; set; }

    public string? CameraStreamUrl { get; set; }

    public string? ThumbnailUrl { get; set; }

    // MMU (Happy Hare) state
    public bool MmuDetected { get; set; }

    public bool MmuEnabled { get; set; }

    public bool MmuIsHomed { get; set; }

    public int MmuActiveTool { get; set; } = -1;

    public int MmuActiveGate { get; set; }

    public string? MmuFilamentState { get; set; }

    public string? MmuAction { get; set; }

    public int MmuNumGates { get; set; }

    public bool MmuHasBypass { get; set; }

    public bool MmuEndlessSpool { get; set; }

    public bool MmuClogDetection { get; set; }

    /// <summary>Per-gate status: 0=empty, 1=available, 2=unknown, -1=disabled.</summary>
    public int[]? MmuGateStatus { get; set; }

    /// <summary>Per-gate material names (e.g., "PLA", "PETG").</summary>
    public string[]? MmuGateMaterial { get; set; }

    /// <summary>Per-gate CSS color strings.</summary>
    public string[]? MmuGateColor { get; set; }

    /// <summary>Per-gate filament brand/name.</summary>
    public string[]? MmuGateFilamentName { get; set; }

    /// <summary>Per-gate Spoolman spool IDs (-1 = none).</summary>
    public int[]? MmuGateSpoolId { get; set; }

    /// <summary>The MMU protocol type. Default <see cref="MmuProtocol.Unknown"/> until protocol is identified.</summary>
    public string MmuType { get; set; } = MmuProtocol.Unknown;

    // ── Qidibox filament box state ──

    /// <summary>Whether a Qidibox filament box has been detected on this printer.</summary>
    public bool QidiboxDetected { get; set; }

    /// <summary>Number of physical boxes (each box has 4 slots).</summary>
    public int QidiboxBoxCount { get; set; }

    /// <summary>Whether the filament dictionary has been fetched from the printer.</summary>
    public bool QidiboxDictFetched { get; set; }

    /// <summary>Number of failed fetch attempts for the filament dictionary.</summary>
    public int QidiboxDictFetchAttempts { get; set; }

    /// <summary>When to next retry fetching the filament dictionary (UTC).</summary>
    public DateTime QidiboxDictRetryAfter { get; set; }

    /// <summary>Filament type index → name mapping from officiall_filas_list.cfg.</summary>
    public Dictionary<int, string> QidiboxFilamentDict { get; set; } = [];

    /// <summary>Color index → hex color string mapping from officiall_filas_list.cfg.</summary>
    public Dictionary<int, string> QidiboxColorDict { get; set; } = [];

    // ── AFC (BoxTurtle/NightOwl/QuattroBox) state ──

    /// <summary>Whether an AFC Klipper add-on has been detected on this printer.</summary>
    public bool AfcDetected { get; set; }

    /// <summary>AFC system state: "Idle", "Loading", "Unloading", "Error", etc.</summary>
    public string? AfcCurrentState { get; set; }

    /// <summary>Name of the currently loaded lane (e.g., "lane1"), or null if none.</summary>
    public string? AfcCurrentLoad { get; set; }

    /// <summary>Whether the AFC system is in an error state.</summary>
    public bool AfcErrorState { get; set; }

    /// <summary>Whether the AFC bypass is active.</summary>
    public bool AfcBypassState { get; set; }

    /// <summary>
    /// Ordered list of AFC lane names for mapping to gate indices.
    /// Thread-safety: single-writer (HandleAfcUpdates on the WebSocket read loop),
    /// read by BuildMmuStatus on the same loop, so no concurrent mutation occurs.
    /// </summary>
    public List<string> AfcLaneNames { get; set; } = [];

    /// <summary>Set to true when any MMU field changes; cleared after BuildMmuStatus builds a new snapshot.</summary>
    public bool MmuDirty { get; set; }

    /// <summary>Cached MmuStatusDto, rebuilt only when <see cref="MmuDirty"/> is set.</summary>
    private MmuStatusDto? _cachedMmuStatus;

    /// <summary>
    /// Builds an <see cref="MmuStatusDto"/> from accumulated state, or null if no MMU detected.
    /// Returns a cached instance when nothing has changed (<see cref="MmuDirty"/> is false).
    /// </summary>
    public MmuStatusDto? BuildMmuStatus()
    {
        if (!MmuDetected)
        {
            return null;
        }

        if (!MmuDirty && _cachedMmuStatus is not null)
        {
            return _cachedMmuStatus;
        }

        int gateCount = MmuNumGates > 0 ? MmuNumGates : 0;
        MmuGateDto[] gates = new MmuGateDto[gateCount];
        for (int i = 0; i < gateCount; i++)
        {
            // Resolve lane/slot name: AFC has real names, Qidibox uses "slot{i}", HappyHare omits.
            string? gateName = MmuType switch
            {
                MmuProtocol.Afc => i < AfcLaneNames.Count ? AfcLaneNames[i] : null,
                MmuProtocol.Qidibox => $"slot{i}",
                _ => null,
            };

            gates[i] = new MmuGateDto(
                Index: i,
                Status: MmuGateStatus is not null && i < MmuGateStatus.Length ? MmuGateStatus[i] : 2,
                Material: MmuGateMaterial is not null && i < MmuGateMaterial.Length ? MmuGateMaterial[i] : null,
                Color: MmuGateColor is not null && i < MmuGateColor.Length ? MmuGateColor[i] : null,
                FilamentName: MmuGateFilamentName is not null && i < MmuGateFilamentName.Length ? MmuGateFilamentName[i] : null,
                SpoolId: MmuGateSpoolId is not null && i < MmuGateSpoolId.Length ? MmuGateSpoolId[i] : -1,
                Name: gateName);
        }

        _cachedMmuStatus = new MmuStatusDto(
            Enabled: MmuEnabled,
            IsHomed: MmuIsHomed,
            ActiveTool: MmuActiveTool,
            ActiveGate: MmuActiveGate,
            FilamentState: MmuFilamentState,
            Action: MmuAction,
            NumGates: gateCount,
            HasBypass: MmuHasBypass,
            EndlessSpool: MmuEndlessSpool,
            ClogDetection: MmuClogDetection,
            Gates: gates,
            MmuType: MmuType);
        MmuDirty = false;
        return _cachedMmuStatus;
    }
}
