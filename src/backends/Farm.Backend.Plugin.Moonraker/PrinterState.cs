using Farm.Infrastructure;

namespace Farm.Backend.Plugin.Moonraker;

// Persistent state for a printer to avoid overwriting good values with nulls
internal sealed class PrinterState
{
    public long? OriginWatermark { get; private set; }

    public void IncludeOrigin(long? originWatermark)
    {
        OriginWatermark = originWatermark;
    }

    public double? X { get; set; }

    public double? Y { get; set; }

    public double? Z { get; set; }

    /// <summary>
    /// Most recent valid G-code-frame position, retained across incremental toolhead-only updates.
    /// </summary>
    public SafetyVector3Dto? GcodePositionMm { get; set; }

    public double? HotendTemp { get; set; }

    public DateTime? HotendTempObservedAtUtc { get; set; }

    public double? BedTemp { get; set; }

    public double? HotendTarget { get; set; }

    public DateTime? HotendTargetObservedAtUtc { get; set; }

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

    public DateTime? HomedAxesObservedAtUtc { get; set; }

    public SafetyVector3Dto? CoordinateOriginOffsetMm { get; set; }

    public DateTime? CoordinateOriginOffsetObservedAtUtc { get; set; }

    public int? CurrentLayer { get; set; }

    public int? TotalLayers { get; set; }

    public double? FanSpeedPercent { get; set; }

    public double? LiveZOffsetMm { get; set; }

    public void ResetLiveReadbacks()
    {
        CurrentLayer = null;
        TotalLayers = null;
        FanSpeedPercent = null;
        LiveZOffsetMm = null;
    }

    /// <summary>
    /// Active Klipper extruder index parsed from the <c>toolhead.extruder</c> field
    /// ("extruder" → 0, "extruder1" → 1, …). Used to attribute per-tool wear on native
    /// multi-extruder toolchangers that do not expose an MMU object (issue #711, round-14).
    /// Null until the printer reports a <c>toolhead.extruder</c> value.
    /// </summary>
    public int? ActiveExtruderIndex { get; set; }

    public string? CameraStreamUrl { get; set; }

    public string? ThumbnailUrl { get; set; }

    public string? ThumbnailCacheIdentity { get; set; }

    public long? ThumbnailFileSize { get; set; }

    public double? ThumbnailFileModified { get; set; }

    public DateTime ThumbnailFileMetadataFetchedAtUtc { get; set; }

    public double? ThumbnailJobStartTime { get; set; }

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

    /// <summary>Material of the external spool holder / bypass (Qidi "Rack"), or null when not reported.</summary>
    public string? MmuBypassMaterial { get; set; }

    /// <summary>CSS color of the external spool holder / bypass (Qidi "Rack"), or null when not reported.</summary>
    public string? MmuBypassColor { get; set; }

    /// <summary>Filament name of the external spool holder / bypass (Qidi "Rack"), or null when not reported.</summary>
    public string? MmuBypassFilamentName { get; set; }

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

    /// <summary>Whether Snapmaker U1 print_task_config lanes have been detected on this printer.</summary>
    public bool SnapmakerU1Detected { get; set; }

    public int SnapmakerU1ActiveTool { get; set; } = -1;

    public SnapmakerU1LaneStatus[] SnapmakerU1Lanes { get; set; } = SnapmakerU1PrintTaskConfigParser.CreateEmptyLanes();

    public bool MergeSnapmakerU1Delta(SnapmakerU1PrintTaskConfigDelta delta)
    {
        SnapmakerU1LaneStatus[] lanes = SnapmakerU1Lanes.Length == SnapmakerU1PrintTaskConfigParser.LaneCount
            ? SnapmakerU1Lanes
            : SnapmakerU1PrintTaskConfigParser.CreateEmptyLanes();

        bool lanesChanged = false;
        foreach (SnapmakerU1LaneDelta laneDelta in delta.Lanes)
        {
            if (laneDelta.Index is < 0 or >= SnapmakerU1PrintTaskConfigParser.LaneCount)
            {
                continue;
            }

            SnapmakerU1LaneStatus current = lanes[laneDelta.Index];
            SnapmakerU1LaneStatus merged = MergeSnapmakerU1Lane(current, laneDelta);
            if (!Equals(current, merged))
            {
                lanes[laneDelta.Index] = merged;
                lanesChanged = true;
            }
        }

        if (delta.ActiveTool is >= 0 and < SnapmakerU1PrintTaskConfigParser.LaneCount &&
            SnapmakerU1ActiveTool != delta.ActiveTool.Value)
        {
            SnapmakerU1ActiveTool = delta.ActiveTool.Value;
            lanesChanged = true;
        }

        for (int i = 0; i < lanes.Length; i++)
        {
            bool isActive = i == SnapmakerU1ActiveTool;
            if (lanes[i].IsActive != isActive)
            {
                lanes[i] = lanes[i] with { IsActive = isActive };
                lanesChanged = true;
            }
        }

        SnapmakerU1Lanes = lanes;
        return lanesChanged;
    }

    private static SnapmakerU1LaneStatus MergeSnapmakerU1Lane(
        SnapmakerU1LaneStatus current,
        SnapmakerU1LaneDelta delta)
    {
        if (delta.Loaded == false)
        {
            return current with
            {
                Loaded = false,
                Material = null,
                SubType = null,
                Color = null,
                Official = false
            };
        }

        return current with
        {
            Loaded = delta.Loaded ?? current.Loaded,
            Material = delta.HasMaterial ? delta.Material : current.Material,
            SubType = delta.HasSubType ? delta.SubType : current.SubType,
            Color = delta.HasColor ? delta.Color : current.Color,
            Official = delta.Official ?? current.Official
        };
    }

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

    // ── ZMOD IFS (Flashforge AD5X) fields — issue #3323 ──

    /// <summary>True once a ZMOD <c>zmod_ifs</c> or <c>zmod_color</c> object has been seen.</summary>
    public bool ZmodIfsDetected { get; set; }

    /// <summary>Last reported <c>zmod_ifs.available</c>; null until reported.</summary>
    public bool? ZmodIfsAvailable { get; set; }

    /// <summary>Last reported <c>zmod_ifs.Ports</c> filament-sensor states, one per IFS port; null entries are malformed/unknown.</summary>
    public bool?[]? ZmodIfsPorts { get; set; }

    /// <summary>Last reported <c>zmod_ifs.Chan</c> (1-based active channel, 0 = none, -1 = unparseable); null until reported.</summary>
    public int? ZmodIfsChannel { get; set; }

    /// <summary>Last reported <c>zmod_color.channel</c> (1-based active channel, -1 = unparseable); null until reported.</summary>
    public int? ZmodColorChannel { get; set; }

    /// <summary>Consecutive status frames that reported <see cref="ZmodGrowthCandidateCount"/> live gates.</summary>
    private int _zmodGrowthStableFrames;

    /// <summary>Live gate count currently being debounced for persisted-row growth (issue #3390).</summary>
    public int ZmodGrowthCandidateCount { get; private set; }

    /// <summary>True once persisted MMU gate growth was attempted for the current connection.</summary>
    public bool ZmodGrowthAttempted { get; private set; }

    /// <summary>Frames the same live gate count must be reported before persisted rows are grown.</summary>
    public const int ZmodGrowthStableFrameThreshold = 3;

    /// <summary>
    /// Debounces the live-reported ZMOD gate count. Returns true exactly once per connection, when the
    /// same live-reported (not defaulted) count has been seen for <see cref="ZmodGrowthStableFrameThreshold"/>
    /// consecutive frames. The count is capped at 16.
    /// </summary>
    public bool TryGetStableZmodGateCount(int frameReportedCount, out int gateCount)
    {
        gateCount = 0;
        if (ZmodGrowthAttempted || frameReportedCount <= 0)
        {
            // Frames that report no count neither advance nor reset the debounce.
            return false;
        }

        int live = Math.Min(frameReportedCount, 16);

        if (live != ZmodGrowthCandidateCount)
        {
            ZmodGrowthCandidateCount = live;
            _zmodGrowthStableFrames = 1;
        }
        else
        {
            _zmodGrowthStableFrames++;
        }

        if (_zmodGrowthStableFrames < ZmodGrowthStableFrameThreshold)
        {
            return false;
        }

        ZmodGrowthAttempted = true;
        gateCount = live;
        return true;
    }

    /// <summary>Highest slot count reported by <c>zmod_color.slots</c>.</summary>
    public int ZmodSlotCount { get; set; }

    /// <summary>Per-slot <c>zmod_color.slots[].hasFilament</c>; fallback presence when ports are absent.</summary>
    public bool?[]? ZmodSlotHasFilament { get; set; }

    /// <summary>Set to true when any MMU field changes; cleared after BuildMmuStatus builds a new snapshot.</summary>
    public bool MmuDirty { get; set; }

    /// <summary>Cached MmuStatusDto, rebuilt only when <see cref="MmuDirty"/> is set.</summary>
    private MmuStatusDto? _cachedMmuStatus;

    /// <summary>
    /// Ensures the per-gate MMU arrays are sized to <paramref name="numSlots"/>, preserving existing
    /// entries. New status entries default to 2 (unknown) and new spool IDs to -1.
    /// </summary>
    public void EnsureMmuGateArrays(int numSlots)
    {
        if (MmuGateStatus == null || MmuGateStatus.Length != numSlots)
        {
            int[] newArr = new int[numSlots];
            Array.Fill(newArr, 2); // Default: Unknown
            if (MmuGateStatus is not null)
            {
                Array.Copy(MmuGateStatus, newArr, Math.Min(MmuGateStatus.Length, numSlots));
            }

            MmuGateStatus = newArr;
        }

        if (MmuGateMaterial == null || MmuGateMaterial.Length != numSlots)
        {
            string[] newArr = new string[numSlots];
            if (MmuGateMaterial is not null)
            {
                Array.Copy(MmuGateMaterial, newArr, Math.Min(MmuGateMaterial.Length, numSlots));
            }

            MmuGateMaterial = newArr;
        }

        if (MmuGateColor == null || MmuGateColor.Length != numSlots)
        {
            string[] newArr = new string[numSlots];
            if (MmuGateColor is not null)
            {
                Array.Copy(MmuGateColor, newArr, Math.Min(MmuGateColor.Length, numSlots));
            }

            MmuGateColor = newArr;
        }

        if (MmuGateFilamentName == null || MmuGateFilamentName.Length != numSlots)
        {
            string[] newArr = new string[numSlots];
            if (MmuGateFilamentName is not null)
            {
                Array.Copy(MmuGateFilamentName, newArr, Math.Min(MmuGateFilamentName.Length, numSlots));
            }

            MmuGateFilamentName = newArr;
        }

        if (MmuGateSpoolId == null || MmuGateSpoolId.Length != numSlots)
        {
            int[] newArr = new int[numSlots];
            Array.Fill(newArr, -1);
            if (MmuGateSpoolId is not null)
            {
                Array.Copy(MmuGateSpoolId, newArr, Math.Min(MmuGateSpoolId.Length, numSlots));
            }

            MmuGateSpoolId = newArr;
        }
    }

    /// <summary>
    /// Clears topology-dependent MMU state before a new Moonraker object subscription snapshot
    /// is applied. A reconnect can discover a different Klipper object set, so retaining fields
    /// from the previous connection would surface a removed or different MMU protocol indefinitely.
    /// </summary>
    public void ResetMmuState()
    {
        MmuDetected = false;
        MmuEnabled = false;
        MmuIsHomed = false;
        MmuActiveTool = -1;
        MmuActiveGate = 0;
        MmuFilamentState = null;
        MmuAction = null;
        MmuNumGates = 0;
        MmuHasBypass = false;
        MmuEndlessSpool = false;
        MmuClogDetection = false;
        MmuGateStatus = null;
        MmuGateMaterial = null;
        MmuGateColor = null;
        MmuGateFilamentName = null;
        MmuGateSpoolId = null;
        MmuBypassMaterial = null;
        MmuBypassColor = null;
        MmuBypassFilamentName = null;
        MmuType = MmuProtocol.Unknown;

        QidiboxDetected = false;
        QidiboxBoxCount = 0;
        QidiboxDictFetched = false;
        QidiboxDictFetchAttempts = 0;
        QidiboxDictRetryAfter = default;
        QidiboxFilamentDict = [];
        QidiboxColorDict = [];

        AfcDetected = false;
        AfcCurrentState = null;
        AfcCurrentLoad = null;
        AfcErrorState = false;
        AfcBypassState = false;
        AfcLaneNames = [];

        SnapmakerU1Detected = false;
        SnapmakerU1ActiveTool = -1;
        SnapmakerU1Lanes = SnapmakerU1PrintTaskConfigParser.CreateEmptyLanes();

        ZmodIfsDetected = false;
        ZmodIfsAvailable = null;
        ZmodIfsPorts = null;
        ZmodIfsChannel = null;
        ZmodColorChannel = null;
        ZmodSlotCount = 0;
        ZmodSlotHasFilament = null;
        ZmodGrowthCandidateCount = 0;
        ZmodGrowthAttempted = false;
        _zmodGrowthStableFrames = 0;

        MmuDirty = true;
        _cachedMmuStatus = null;
    }

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

                // Snapmaker U1 touchscreen labels are T1-T4, while Moonraker/API extruders are zero-based T0-T3.
                // This mapping is inferred from U1Hub/SnapCon behavior and needs real-hardware confirmation.
                MmuProtocol.SnapmakerU1 => $"T{i + 1}",

                // ZMOD IFS slots are reported with 1-based IDs matching the AD5X touchscreen.
                MmuProtocol.ZmodIfs => $"Gate {i + 1}",
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
            MmuType: MmuType,
            BypassMaterial: MmuBypassMaterial,
            BypassColor: MmuBypassColor,
            BypassFilamentName: MmuBypassFilamentName);
        MmuDirty = false;
        return _cachedMmuStatus;
    }
}
