namespace Farm.Moonraker.Emulator.Domain;

/// <summary>
/// Which MMU/filament-changer wire protocol this printer's <see cref="MmuFixture"/> currently
/// emulates. Selected at runtime through the control API
/// (<c>POST /__emulator/printer/mmu</c>) rather than fixed per scenario, since a real MMU
/// attachment is orthogonal to the Klippy/print-state scenario (ready/printing/paused/shutdown).
/// </summary>
public enum MmuMode
{
    /// <summary>No MMU attached — the emulator omits every MMU-shaped object entirely.</summary>
    None,

    /// <summary>
    /// Happy Hare MMU: emits a single <c>mmu</c> Klipper object, consumed by
    /// <c>MoonrakerSubscriptionService.HandleMmuUpdate</c>.
    /// </summary>
    HappyHare,

    /// <summary>
    /// AFC (BoxTurtle/NightOwl/QuattroBox) filament changer: emits a top-level <c>AFC</c> object
    /// plus one <c>AFC_stepper &lt;lane&gt;</c> object per lane, consumed by
    /// <c>MoonrakerSubscriptionService.HandleAfcUpdates</c>. Entirely self-contained within
    /// <c>printer.objects.subscribe</c>/query status — no extra REST route is required, unlike
    /// Qidibox below.
    /// </summary>
    Afc,

    /// <summary>
    /// Qidibox filament box: emits <c>box_stepper slotN</c> status objects plus a
    /// <c>save_variables</c> object (<c>box_count</c>/<c>last_load_slot</c>/
    /// <c>filament_slotN</c>/<c>color_slotN</c>), consumed by
    /// <c>MoonrakerSubscriptionService.HandleQidiboxUpdatesAsync</c>. Unlike Happy Hare/AFC, the
    /// real parser also fetches <c>GET server/files/config/officiall_filas_list.cfg</c> via a raw
    /// <see cref="System.Net.Http.HttpClient"/> call (not through <c>MoonrakerClient</c>) to
    /// resolve the integer filament/color codes in <c>save_variables</c> into names/hex colors —
    /// the emulator seeds that file into the "config" root (see <c>PrinterRegistry</c>) and
    /// serves it through a matching generic config-root download route
    /// (<c>GetConfigFileAsync</c>).
    /// </summary>
    Qidibox,

    /// <summary>
    /// Snapmaker U1: exposes four *physical* toolheads (not MMU virtual gates) through the
    /// ordinary <c>toolhead.extruder</c> field (the active physical extruder's Klipper name,
    /// e.g. <c>"extruder1"</c>) plus a <c>print_task_config</c> object carrying parallel
    /// <c>filament_exist</c>/<c>filament_color_rgba</c>/<c>filament_type</c>/
    /// <c>filament_sub_type</c>/<c>filament_official</c> arrays, consumed by
    /// <c>SnapmakerU1PrintTaskConfigParser</c>/
    /// <c>MoonrakerSubscriptionService.HandleSnapmakerU1PrintTaskConfigUpdateAsync</c>. Entirely
    /// self-contained within <c>printer.objects.subscribe</c>/query status — no extra REST route
    /// is required. Mutually exclusive with Happy Hare/AFC/Qidibox at the real parser's state
    /// level (a printer has either a Klipper MMU attachment or Snapmaker U1's native physical
    /// toolheads, never both), which this fixture's single <see cref="MmuMode"/> already enforces.
    /// </summary>
    SnapmakerU1,
}

/// <summary>
/// Optional MMU/filament-changer fixture. <see cref="Mode"/> is <see cref="MmuMode.None"/> (no
/// MMU-shaped objects emitted at all) for every seeded scenario by default, and is switched at
/// runtime through the control API (<c>POST /__emulator/printer/mmu</c>, gated the same as every
/// other <c>/__emulator/**</c> route) for tests that specifically exercise MMU-consuming code
/// paths. <see cref="PrinterAggregate.BuildObjectsSnapshot"/> reads this fixture and emits the
/// wire shape matching <see cref="Mode"/>. All four variants the backend plugin consumes — Happy
/// Hare, AFC, Qidibox, and Snapmaker U1 — are modeled; see each <see cref="MmuMode"/> member for
/// its exact wire shape and, for Qidibox, its extra config-root file dependency.
/// </summary>
public sealed class MmuFixture
{
    public MmuMode Mode { get; set; } = MmuMode.None;

    /// <summary>True whenever any MMU-shaped object should be emitted (i.e. <see cref="Mode"/> is not <see cref="MmuMode.None"/>).</summary>
    public bool Detected => Mode != MmuMode.None;

    // ---- Happy Hare ("mmu" object) fields ----
    public bool Enabled { get; set; } = true;

    public bool IsHomed { get; set; } = true;

    public int ActiveTool { get; set; }

    public int ActiveGate { get; set; }

    public string FilamentState { get; set; } = "Loaded";

    public string Action { get; set; } = "Idle";

    public int NumGates { get; set; } = 4;

    public bool HasBypass { get; set; } = true;

    public bool EndlessSpool { get; set; }

    public bool ClogDetection { get; set; } = true;

    public int[] GateStatus { get; set; } = [1, 1, 0, -1];

    public string?[] GateMaterial { get; set; } = ["PLA", "PETG", null, null];

    public string?[] GateColor { get; set; } = ["#FF0000", "#00A0FF", null, null];

    public string?[] GateFilamentName { get; set; } = ["Generic PLA", "Generic PETG", null, null];

    /// <summary>Spoolman spool id per gate, -1 when no spool is assigned to that gate.</summary>
    public int[] GateSpoolId { get; set; } = [101, 102, -1, -1];

    // ---- AFC ("AFC" + "AFC_stepper <lane>" objects) fields ----
    // AFC reuses the Gate* arrays above for per-lane material/color/spool_id/status (indexed the
    // same way Happy Hare's gates are), since both are conceptually "one filament source per
    // index" — only the wire shape emitted for each differs (see BuildObjectsSnapshot).
    public string[] LaneNames { get; set; } = ["lane1", "lane2", "lane3", "lane4"];

    public string AfcCurrentState { get; set; } = "Idle";

    public string? AfcCurrentLoad { get; set; } = "lane1";

    public bool AfcErrorState { get; set; }

    public bool AfcBypassState { get; set; }

    // ---- Qidibox ("box_stepper slotN" + "save_variables" objects) fields ----
    // save_variables carries integer filament/color *codes* per slot (not names/hex strings
    // directly) that the real client resolves against officiall_filas_list.cfg's [colordict]/
    // [filaN] sections — see PrinterRegistry's seeded "config" root file and
    // QidiboxColorDictIni/QidiboxFilamentDictIni below for the matching dictionary content.
    public int QidiboxBoxCount { get; set; } = 1;

    /// <summary>"slotN" naming the currently loaded slot, matching the parser's "slot" + int.Parse(rest) convention.</summary>
    public string QidiboxLastLoadSlot { get; set; } = "slot0";

    /// <summary>Filament dict index per slot (looked up in officiall_filas_list.cfg's [filaN] sections); 0 = unmapped.</summary>
    public int[] QidiboxFilamentTypeCodes { get; set; } = [1, 2, 0, 0];

    /// <summary>Color dict index per slot (looked up in officiall_filas_list.cfg's [colordict] section); 0 = unmapped.</summary>
    public int[] QidiboxColorCodes { get; set; } = [1, 2, 0, 0];

    /// <summary><c>box_stepper slotN</c>'s runout_button: 0 = filament present, 1 = empty, null = slot physically disabled.</summary>
    public int?[] QidiboxRunoutButton { get; set; } = [0, 0, 1, null];

    /// <summary>
    /// Deterministic content for the "config" root's <c>officiall_filas_list.cfg</c>, matching
    /// the codes above: dict index 1 -&gt; PLA/#FF0000, index 2 -&gt; PETG/#00A0FF (the same
    /// PLA/PETG theme Happy Hare's/AFC's seeded gates use, for cross-protocol consistency).
    /// </summary>
    public const string QidiboxDictionaryIniContent =
        "[colordict]\n" +
        "1 = #FF0000\n" +
        "2 = #00A0FF\n" +
        "\n" +
        "[fila1]\n" +
        "filament = PLA\n" +
        "\n" +
        "[fila2]\n" +
        "filament = PETG\n";

    // ---- Snapmaker U1 ("toolhead.extruder" + "print_task_config" object) fields ----
    // Snapmaker U1 exposes four *physical* toolheads, not MMU virtual gates, but reuses the same
    // per-index-source concept: filament_type/filament_color_rgba/filament_sub_type are the
    // Snapmaker-native equivalents of Happy Hare's/AFC's GateMaterial/GateColor/GateFilamentName.

    /// <summary>
    /// Index (0-3) of the currently active physical extruder. Serialized as
    /// <c>toolhead.extruder</c> = <c>"extruder"</c> for index 0 or <c>"extruderN"</c> for index N,
    /// matching <c>SnapmakerU1PrintTaskConfigParser.ReadExtruderIndex</c>'s accepted formats.
    /// </summary>
    public int SnapmakerU1ActiveToolheadIndex { get; set; } = 1;

    public bool[] SnapmakerU1FilamentExist { get; set; } = [true, true, false, false];

    /// <summary>Filament material per physical toolhead; "NONE" is treated as empty by the real parser.</summary>
    public string[] SnapmakerU1FilamentType { get; set; } = ["PLA", "PETG", "NONE", "NONE"];

    /// <summary>Filament sub-type per physical toolhead; "NONE" is normalized to null by the real parser.</summary>
    public string[] SnapmakerU1FilamentSubType { get; set; } = ["NONE", "NONE", "NONE", "NONE"];

    /// <summary>RGBA hex per physical toolhead (8 hex chars, no '#'); the real parser drops the alpha byte.</summary>
    public string[] SnapmakerU1FilamentColorRgba { get; set; } = ["FF0000FF", "00A0FFFF", "00000000", "00000000"];

    public bool[] SnapmakerU1FilamentOfficial { get; set; } = [true, true, false, false];

    public void Reset()
    {
        Mode = MmuMode.None;
        Enabled = true;
        IsHomed = true;
        ActiveTool = 0;
        ActiveGate = 0;
        FilamentState = "Loaded";
        Action = "Idle";
        NumGates = 4;
        HasBypass = true;
        EndlessSpool = false;
        ClogDetection = true;
        GateStatus = [1, 1, 0, -1];
        GateMaterial = ["PLA", "PETG", null, null];
        GateColor = ["#FF0000", "#00A0FF", null, null];
        GateFilamentName = ["Generic PLA", "Generic PETG", null, null];
        GateSpoolId = [101, 102, -1, -1];
        LaneNames = ["lane1", "lane2", "lane3", "lane4"];
        AfcCurrentState = "Idle";
        AfcCurrentLoad = "lane1";
        AfcErrorState = false;
        AfcBypassState = false;
        QidiboxBoxCount = 1;
        QidiboxLastLoadSlot = "slot0";
        QidiboxFilamentTypeCodes = [1, 2, 0, 0];
        QidiboxColorCodes = [1, 2, 0, 0];
        QidiboxRunoutButton = [0, 0, 1, null];
        SnapmakerU1ActiveToolheadIndex = 1;
        SnapmakerU1FilamentExist = [true, true, false, false];
        SnapmakerU1FilamentType = ["PLA", "PETG", "NONE", "NONE"];
        SnapmakerU1FilamentSubType = ["NONE", "NONE", "NONE", "NONE"];
        SnapmakerU1FilamentColorRgba = ["FF0000FF", "00A0FFFF", "00000000", "00000000"];
        SnapmakerU1FilamentOfficial = [true, true, false, false];
    }
}
