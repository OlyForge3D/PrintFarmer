using System.Net.WebSockets;
using System.Text.RegularExpressions;

namespace Farm.Moonraker.Emulator.Domain;

/// <summary>Thrown when a REST command is rejected because the printer/firmware is busy (maps to HTTP 409).</summary>
public sealed class PrinterBusyException : Exception
{
    public PrinterBusyException()
    {
    }

    public PrinterBusyException(string message)
        : base(message)
    {
    }

    public PrinterBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when a REST command cannot run because Klippy is not connected (maps to HTTP 503).</summary>
public sealed class KlippyUnavailableException : Exception
{
    public KlippyUnavailableException()
    {
    }

    public KlippyUnavailableException(string message)
        : base(message)
    {
    }

    public KlippyUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when <c>printer/print/start</c> is asked to print a filename that does not exist in the
/// virtual "gcodes" root (maps to HTTP 404). Real Moonraker/Klipper cannot start a print job for a
/// file it cannot find on disk, so the emulator must not fabricate success for one either.
/// </summary>
public sealed class PrintFileNotFoundException : Exception
{
    public PrintFileNotFoundException()
    {
    }

    public PrintFileNotFoundException(string message)
        : base(message)
    {
    }

    public PrintFileNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a gcode command carries an out-of-bounds or unknown parameter (e.g. an MMU tool
/// index outside the configured gate count, or an AFC lane name that doesn't exist), or targets
/// an MMU-specific macro while a different (or no) MMU mode is active. Maps to HTTP 400 — real
/// Klipper/Moonraker would reject these the same way rather than silently succeeding.
/// </summary>
public sealed class GcodeParameterException : Exception
{
    public GcodeParameterException()
    {
    }

    public GcodeParameterException(string message)
        : base(message)
    {
    }

    public GcodeParameterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An active WebSocket connection and its object subscription set.</summary>
public sealed class WsSubscription
{
    public required WebSocket Socket { get; init; }

    /// <summary>Subscribed object name -&gt; requested field filter (null = all fields).</summary>
    public Dictionary<string, string[]?> Objects { get; } = new(StringComparer.Ordinal);

    public bool CameraMonitoring { get; set; }

    /// <summary>Set by a "stale notification" fault rule to suppress broadcasts until this time.</summary>
    public DateTimeOffset? SuppressNotificationsUntil { get; set; }

    public SemaphoreSlim SendGate { get; } = new(1, 1);

    public Dictionary<string, Dictionary<string, string>> LastFieldValues { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The full mutable state of one emulated Moonraker printer: Klippy/connection state,
/// the active print job, temperatures, position, files, history, webcams, Spoolman,
/// exclude-object bookkeeping, and an optional MMU fixture. One instance exists per
/// seeded scenario (ready/printing/paused/shutdown) for the lifetime of the process,
/// or until reset through the control API.
/// </summary>
public sealed class PrinterAggregate
{
    private readonly object _stateLock = new();

    public required string Id { get; init; }

    /// <summary>
    /// This printer's display/host name, reported verbatim as <c>printer/info</c>'s
    /// <c>hostname</c> field. One process instance emulates exactly one printer, so
    /// there is no per-request host dispatch — this is simply what the emulator calls
    /// itself.
    /// </summary>
    public required string Name { get; init; }

    public PrinterScenario InitialScenario { get; init; }

    public VirtualClock Clock { get; } = new();

    public VirtualFileSystem Files { get; } = new();

    public HistoryStore History { get; } = new();

    public List<WebcamFixture> Webcams { get; } = [];

    public SpoolmanFixture Spoolman { get; } = new();

    public MmuFixture Mmu { get; } = new();

    public ConcurrentDictionary<Guid, WsSubscription> Connections { get; } = new();

    // ---- Klippy / connection state ----
    public string KlippyState { get; private set; } = "ready";

    public string KlippyStateMessage { get; private set; } = "Printer is ready and A okay!";

    // ---- print_stats ----
    public string PrintState { get; private set; } = "standby";

    public string? Filename { get; private set; }

    public double PrintDuration { get; private set; }

    public double TotalDuration { get; private set; }

    public double FilamentUsed { get; private set; }

    public string PrintMessage { get; private set; } = string.Empty;

    private DateTimeOffset? _printStartedAt;
    private double _totalDurationOffset;

    private const double SimulatedPrintTotalSeconds = 600;

    // ---- toolhead / gcode_move / motion ----
    public double[] Position { get; private set; } = [120.0, 120.0, 5.0, 0.0];

    public string HomedAxes { get; private set; } = "xyz";

    // ---- temperatures ----
    public double ExtruderTemperature { get; private set; } = 23.4;

    public double ExtruderTarget { get; private set; }

    public double BedTemperature { get; private set; } = 22.1;

    public double BedTarget { get; private set; }

    // ---- exclude_object ----
    public List<string> AvailableObjects { get; } = [];

    public List<string> ExcludedObjects { get; } = [];

    public string? CurrentObject { get; private set; }

    /// <summary>Tracks the last G90/G91 mode sent, reflected in gcode_move.absolute_coordinates.</summary>
    public bool AbsoluteCoordinates { get; private set; } = true;

    /// <summary>Resets this printer back to the scenario it was originally seeded with.</summary>
    public void ResetToInitial() => ResetToScenario(InitialScenario);

    public void ResetToScenario(PrinterScenario scenario)
    {
        lock (_stateLock)
        {
            Clock.Reset();
            _printStartedAt = null;
            _totalDurationOffset = 0;
            ExtruderTarget = 0;
            BedTarget = 0;
            ExtruderTemperature = 23.4;
            BedTemperature = 22.1;
            Position = [120.0, 120.0, 5.0, 0.0];
            HomedAxes = "xyz";
            AbsoluteCoordinates = true;
            ExcludedObjects.Clear();
            AvailableObjects.Clear();
            CurrentObject = null;
            PrintMessage = string.Empty;

            switch (scenario)
            {
                case PrinterScenario.Ready:
                    KlippyState = "ready";
                    KlippyStateMessage = "Printer is ready and A okay!";
                    PrintState = "standby";
                    Filename = null;
                    PrintDuration = 0;
                    TotalDuration = 0;
                    FilamentUsed = 0;
                    break;
                case PrinterScenario.Printing:
                    KlippyState = "ready";
                    KlippyStateMessage = "Printer is ready and A okay!";
                    PrintState = "printing";
                    Filename = "benchy.gcode";
                    ExtruderTarget = 215;
                    BedTarget = 60;
                    ExtruderTemperature = 214.6;
                    BedTemperature = 59.8;
                    AvailableObjects.AddRange(["benchy_hull", "benchy_cabin"]);
                    _printStartedAt = Clock.UtcNow;
                    PrintDuration = 0;
                    TotalDuration = 0;
                    FilamentUsed = 0;
                    break;
                case PrinterScenario.Paused:
                    KlippyState = "ready";
                    KlippyStateMessage = "Printer is ready and A okay!";
                    PrintState = "paused";
                    Filename = "benchy.gcode";
                    ExtruderTarget = 215;
                    BedTarget = 60;
                    ExtruderTemperature = 210.0;
                    BedTemperature = 59.5;
                    AvailableObjects.AddRange(["benchy_hull", "benchy_cabin"]);
                    PrintDuration = 120;
                    TotalDuration = 130;
                    _totalDurationOffset = 10;
                    FilamentUsed = 340.5;
                    break;
                case PrinterScenario.Shutdown:
                    KlippyState = "shutdown";
                    KlippyStateMessage = "Klippy has shutdown. Check klippy.log for more information.";
                    PrintState = "error";
                    Filename = null;
                    PrintDuration = 0;
                    TotalDuration = 0;
                    FilamentUsed = 0;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown printer scenario.");
            }
        }
    }

    /// <summary>Recomputes print_duration/filament_used from elapsed virtual time while a print is active.</summary>
    public void Tick()
    {
        lock (_stateLock)
        {
            if (PrintState != "printing" || _printStartedAt is null)
            {
                return;
            }

            double elapsed = (Clock.UtcNow - _printStartedAt.Value).TotalSeconds;
            PrintDuration = Math.Clamp(elapsed, 0, SimulatedPrintTotalSeconds);
            TotalDuration = _totalDurationOffset + PrintDuration;
            FilamentUsed = Math.Round(PrintDuration / SimulatedPrintTotalSeconds * 1200.0, 2);
            if (PrintDuration >= SimulatedPrintTotalSeconds)
            {
                CompletePrintLocked();
            }
        }
    }

    public double Progress()
    {
        lock (_stateLock)
        {
            if (PrintState is not ("printing" or "paused") || TotalDuration <= 0)
            {
                return PrintState == "complete" ? 1.0 : 0.0;
            }

            return Math.Clamp(PrintDuration / SimulatedPrintTotalSeconds, 0.0, 1.0);
        }
    }

    public void RequireKlippyReady()
    {
        if (KlippyState != "ready")
        {
            throw new KlippyUnavailableException("Klippy is not connected");
        }
    }

    public void StartPrint(string filename)
    {
        lock (_stateLock)
        {
            RequireKlippyReady();
            if (PrintState is "printing" or "paused")
            {
                throw new PrinterBusyException("Print already in progress");
            }

            // Real Moonraker/Klipper cannot start a print for a file that isn't on disk under the
            // gcodes root; the emulator must fail the same way rather than fabricating a success
            // response for a nonexistent filename.
            if (!Files.TryGet("gcodes", filename, out _))
            {
                throw new PrintFileNotFoundException($"print_start: file '{filename}' does not exist");
            }

            Filename = filename;
            PrintState = "printing";
            PrintDuration = 0;
            TotalDuration = 0;
            _totalDurationOffset = 0;
            FilamentUsed = 0;
            ExcludedObjects.Clear();
            CurrentObject = null;
            _printStartedAt = Clock.UtcNow;
        }
    }

    public void Pause()
    {
        lock (_stateLock)
        {
            RequireKlippyReady();
            if (PrintState == "paused")
            {
                return; // idempotent no-op
            }

            if (PrintState != "printing")
            {
                throw new PrinterBusyException("The printer is not currently printing");
            }

            PrintState = "paused";
        }
    }

    public void Resume()
    {
        lock (_stateLock)
        {
            RequireKlippyReady();
            if (PrintState == "printing")
            {
                return; // idempotent no-op
            }

            if (PrintState != "paused")
            {
                throw new PrinterBusyException("The printer is not paused");
            }

            PrintState = "printing";
            _totalDurationOffset = Math.Max(0, TotalDuration - PrintDuration);
            _printStartedAt = Clock.UtcNow - TimeSpan.FromSeconds(PrintDuration);
        }
    }

    public void Cancel()
    {
        lock (_stateLock)
        {
            RequireKlippyReady();
            if (PrintState is "standby" or "cancelled" or "complete")
            {
                return; // idempotent no-op
            }

            RecordHistoryLocked("cancelled");
            PrintState = "cancelled";
            _printStartedAt = null;
        }
    }

    private void CompletePrintLocked()
    {
        RecordHistoryLocked("completed");
        PrintState = "complete";
        _printStartedAt = null;
    }

    private void RecordHistoryLocked(string status)
    {
        if (Filename is null)
        {
            return;
        }

        DateTimeOffset now = Clock.UtcNow;
        History.Add(new HistoryJobEntry
        {
            // Deterministic and monotonic per process, not a random GUID fragment — see
            // HistoryStore.NextJobId. Reproducible for a fixed sequence of print
            // start/cancel/complete operations, which API/UI assertions can rely on.
            JobId = History.NextJobId(),
            Filename = Filename,
            StartTime = (_printStartedAt ?? now).ToUnixTimeSeconds(),
            EndTime = now.ToUnixTimeSeconds(),
            PrintDuration = PrintDuration,
            TotalDuration = TotalDuration,
            FilamentUsed = FilamentUsed,
            Status = status,
        });
    }

    /// <summary>
    /// Applies one or more newline-separated gcode commands, matching what
    /// <c>MoonrakerClient</c> actually sends to <c>/printer/gcode/script</c> for the
    /// operations the consuming UI exposes today:
    /// <list type="bullet">
    ///   <item><c>M112</c> — emergency stop: Klippy state -&gt; "shutdown", print state -&gt; "error". Bypasses the Klippy-ready gate (an e-stop must work even when Klippy is already unwell).</item>
    ///   <item><c>FIRMWARE_RESTART</c> / <c>RESTART</c> — recovery: Klippy state -&gt; "ready", print job cleared back to "standby", homed axes cleared (a real MCU reboot loses homing). Also bypasses the ready gate — this is the recovery path itself.</item>
    ///   <item><c>EXCLUDE_OBJECT NAME=...</c> — adds to <see cref="ExcludedObjects"/> (existing behavior).</item>
    ///   <item><c>G28</c>, <c>G28 X Y</c>, <c>G28 Z</c> — homes the specified axes (all three when bare) into <see cref="HomedAxes"/> and updates <see cref="Position"/>; still refused with <see cref="PrinterBusyException"/> while printing.</item>
    ///   <item><c>M104 S...</c> / <c>M140 S...</c> — set <see cref="ExtruderTarget"/> / <see cref="BedTarget"/>.</item>
    ///   <item><c>G91 G0 X.. Y.. Z.. F..</c> followed by a bare <c>G90</c> (relative move, <c>MoveAsync</c>'s shape) and bare <c>G90 G0 X.. Y.. Z.. F..</c> (absolute move, <c>MoveToAsync</c>'s shape) — both update <see cref="Position"/> and <see cref="AbsoluteCoordinates"/>.</item>
    /// </list>
    /// <b>Documented fidelity boundary:</b> <c>M84</c> (disable motors), <c>LOAD_FILAMENT</c>,
    /// <c>UNLOAD_FILAMENT</c>, and <c>M600</c> (filament change) are acknowledged as
    /// successful no-ops — they still pass through the Klippy-ready gate and any active
    /// scenario/fault rules, but do not mutate any observable emulator state, because the
    /// currently consuming UI has no corresponding "motors enabled" or "filament loaded"
    /// flag to assert against. If a future consumer needs that state, extend this method
    /// alongside the new observable field.
    /// </summary>
    public void SendGcode(string script)
    {
        lock (_stateLock)
        {
            foreach (string rawLine in script.Split('\n'))
            {
                string trimmed = rawLine.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (trimmed.Equals("M112", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyEmergencyStopLocked();
                    continue;
                }

                if (trimmed.Equals("FIRMWARE_RESTART", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals("RESTART", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyFirmwareRestartLocked();
                    continue;
                }

                // Every other consumed command requires Klippy to be connected, matching
                // real Moonraker's 503 "Klippy is not connected" behavior.
                RequireKlippyReady();

                if (trimmed.StartsWith("EXCLUDE_OBJECT", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyExcludeObjectLocked(trimmed);
                    continue;
                }

                if (trimmed.Equals("G28", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("G28 ", StringComparison.OrdinalIgnoreCase))
                {
                    if (PrintState == "printing")
                    {
                        throw new PrinterBusyException("Printer is busy, cannot home while printing");
                    }

                    ApplyHomingLocked(trimmed);
                    continue;
                }

                if (TryParseSValue(trimmed, "M104", out double hotendTarget))
                {
                    ExtruderTarget = hotendTarget;
                    continue;
                }

                if (TryParseSValue(trimmed, "M140", out double bedTarget))
                {
                    BedTarget = bedTarget;
                    continue;
                }

                if (trimmed.Equals("G91", StringComparison.OrdinalIgnoreCase))
                {
                    AbsoluteCoordinates = false;
                    continue;
                }

                if (trimmed.Equals("G90", StringComparison.OrdinalIgnoreCase))
                {
                    AbsoluteCoordinates = true;
                    continue;
                }

                if (trimmed.StartsWith("G91 G0", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyMoveLocked(trimmed, relative: true);
                    continue;
                }

                if (trimmed.StartsWith("G90 G0", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyMoveLocked(trimmed, relative: false);
                    continue;
                }

                // ---- Happy Hare MMU control macros (PrintersController's MmuControlBox) ----
                if (trimmed.StartsWith("MMU_CHANGE_TOOL", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyMmuChangeToolLocked(trimmed);
                    continue;
                }

                if (trimmed.StartsWith("MMU_SELECT_TOOL", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyMmuSelectToolLocked(trimmed);
                    continue;
                }

                if (trimmed.Equals("MMU_LOAD", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyMmuLoadLocked();
                    continue;
                }

                if (trimmed.Equals("MMU_EJECT", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyMmuEjectLocked();
                    continue;
                }

                if (trimmed.Equals("MMU_HOME", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyMmuHomeLocked();
                    continue;
                }

                if (trimmed.Equals("MMU_RECOVER", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyMmuRecoverLocked();
                    continue;
                }

                // ---- Qidibox gate control macros (PrintersController's mmu/gate-action) ----
                if (Regex.IsMatch(trimmed, @"^T(\d+)$", RegexOptions.IgnoreCase))
                {
                    ApplyQidiboxLoadLocked(trimmed);
                    continue;
                }

                if (Regex.IsMatch(trimmed, @"^UNLOAD_T(\d+)$", RegexOptions.IgnoreCase))
                {
                    ApplyQidiboxUnloadLocked(trimmed);
                    continue;
                }

                if (Regex.IsMatch(trimmed, @"^EJECT_T(\d+)$", RegexOptions.IgnoreCase))
                {
                    ApplyQidiboxEjectLocked(trimmed);
                    continue;
                }

                // ---- AFC lane control macros (PrintersController's mmu/gate-action) ----
                if (trimmed.StartsWith("CHANGE_TOOL", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyAfcChangeToolLocked(trimmed);
                    continue;
                }

                if (trimmed.StartsWith("TOOL_UNLOAD", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyAfcToolUnloadLocked(trimmed);
                }

                // M84, LOAD_FILAMENT, UNLOAD_FILAMENT, M600: acknowledged no-ops — see
                // the documented fidelity boundary above.
            }
        }
    }

    private void ApplyEmergencyStopLocked()
    {
        KlippyState = "shutdown";
        KlippyStateMessage = "Emergency stop invoked (M112). Klippy has shutdown. Check klippy.log for more information.";
        PrintState = "error";
    }

    private void ApplyFirmwareRestartLocked()
    {
        KlippyState = "ready";
        KlippyStateMessage = "Printer is ready and A okay!";
        PrintState = "standby";
        Filename = null;
        PrintDuration = 0;
        TotalDuration = 0;
        FilamentUsed = 0;
        _printStartedAt = null;
        HomedAxes = string.Empty;
        ExcludedObjects.Clear();
        CurrentObject = null;
        ExtruderTarget = 0;
        BedTarget = 0;
        AbsoluteCoordinates = true;
    }

    private void ApplyExcludeObjectLocked(string trimmed)
    {
        string? name = ParseExcludeObjectName(trimmed);
        if (name is null)
        {
            return;
        }

        if (!ExcludedObjects.Contains(name, StringComparer.Ordinal))
        {
            ExcludedObjects.Add(name);
        }

        CurrentObject = name;
    }

    private void ApplyHomingLocked(string trimmed)
    {
        string remainder = trimmed.Length > 3 ? trimmed[3..].Trim() : string.Empty;
        IEnumerable<char> requestedAxes = remainder.Length == 0
            ? "xyz"
            : remainder.ToLowerInvariant().Where(c => c is 'x' or 'y' or 'z');
        HashSet<char> newlyHomed = requestedAxes.ToHashSet();

        HashSet<char> combined = HomedAxes.ToLowerInvariant().Where(c => c is 'x' or 'y' or 'z').ToHashSet();
        combined.UnionWith(newlyHomed);
        HomedAxes = string.Concat("xyz".Where(combined.Contains));

        // Homing parks the toolhead at its resting position for the axes just homed.
        double[] position = (double[])Position.Clone();
        foreach (char axis in newlyHomed)
        {
            int index = axis switch { 'x' => 0, 'y' => 1, _ => 2 };
            position[index] = index == 2 ? 5.0 : 120.0;
        }

        Position = position;
    }

    private void ApplyMoveLocked(string line, bool relative)
    {
        double[] position = (double[])Position.Clone();
        if (ParseAxisValue(line, 'X') is { } x)
        {
            position[0] = relative ? position[0] + x : x;
        }

        if (ParseAxisValue(line, 'Y') is { } y)
        {
            position[1] = relative ? position[1] + y : y;
        }

        if (ParseAxisValue(line, 'Z') is { } z)
        {
            position[2] = relative ? position[2] + z : z;
        }

        Position = position;
        AbsoluteCoordinates = !relative;
    }

    // ---- Happy Hare MMU control macros ----
    // Consumed by PrintersController's MmuControlBox endpoints (mmu/change-tool,
    // mmu/select-tool, mmu/load, mmu/eject, mmu/home, mmu/recover). These must produce real,
    // observable fixture transitions rather than acknowledged no-ops.
    private void ApplyMmuChangeToolLocked(string trimmed)
    {
        int tool = RequireHappyHareToolIndex(trimmed, "MMU_CHANGE_TOOL");
        Mmu.ActiveTool = tool;
        Mmu.ActiveGate = tool;
        Mmu.FilamentState = "Loaded";
        Mmu.Action = "Idle";
    }

    private void ApplyMmuSelectToolLocked(string trimmed)
    {
        int tool = RequireHappyHareToolIndex(trimmed, "MMU_SELECT_TOOL");
        Mmu.ActiveTool = tool;
        Mmu.ActiveGate = tool;

        // Pre-select only: distinct from MMU_CHANGE_TOOL, filament is not fed to the extruder.
        Mmu.FilamentState = "Unloaded";
        Mmu.Action = "Idle";
    }

    private void ApplyMmuLoadLocked()
    {
        RequireHappyHare("MMU_LOAD");
        Mmu.FilamentState = "Loaded";
        Mmu.Action = "Idle";
    }

    private void ApplyMmuEjectLocked()
    {
        RequireHappyHare("MMU_EJECT");
        Mmu.FilamentState = "Unloaded";
        Mmu.ActiveTool = -1;
        Mmu.ActiveGate = -1;
        Mmu.Action = "Idle";
    }

    private void ApplyMmuHomeLocked()
    {
        RequireHappyHare("MMU_HOME");
        Mmu.IsHomed = true;
        Mmu.Action = "Idle";
    }

    private void ApplyMmuRecoverLocked()
    {
        RequireHappyHare("MMU_RECOVER");
        Mmu.Action = "Idle";
    }

    private void RequireHappyHare(string command)
    {
        if (Mmu.Mode != MmuMode.HappyHare)
        {
            throw new GcodeParameterException($"{command} requires MMU mode HappyHare (current: {Mmu.Mode}).");
        }
    }

    private int RequireHappyHareToolIndex(string trimmed, string command)
    {
        RequireHappyHare(command);
        if (!TryParseIntParameter(trimmed, "TOOL=", out int tool) || tool < 0 || tool >= Mmu.NumGates)
        {
            throw new GcodeParameterException($"{command}: TOOL must be an integer between 0 and {Mmu.NumGates - 1}.");
        }

        return tool;
    }

    // ---- Qidibox gate control macros ----
    // Consumed by PrintersController's mmu/gate-action endpoint (protocol=qidibox).
    private void ApplyQidiboxLoadLocked(string trimmed)
    {
        int slot = RequireQidiboxSlotIndex(trimmed, @"^T(\d+)$", "T{n}");
        Mmu.QidiboxLastLoadSlot = $"slot{slot}";
    }

    private void ApplyQidiboxUnloadLocked(string trimmed)
    {
        int slot = RequireQidiboxSlotIndex(trimmed, @"^UNLOAD_T(\d+)$", "UNLOAD_T{n}");
        if (Mmu.QidiboxLastLoadSlot == $"slot{slot}")
        {
            Mmu.QidiboxLastLoadSlot = "slot-1";
        }
    }

    private void ApplyQidiboxEjectLocked(string trimmed)
    {
        int slot = RequireQidiboxSlotIndex(trimmed, @"^EJECT_T(\d+)$", "EJECT_T{n}");
        if (slot < Mmu.QidiboxRunoutButton.Length)
        {
            Mmu.QidiboxRunoutButton[slot] = 1; // ejected: slot now empty
        }

        if (Mmu.QidiboxLastLoadSlot == $"slot{slot}")
        {
            Mmu.QidiboxLastLoadSlot = "slot-1";
        }
    }

    private int RequireQidiboxSlotIndex(string trimmed, string pattern, string commandShape)
    {
        if (Mmu.Mode != MmuMode.Qidibox)
        {
            throw new GcodeParameterException($"{commandShape} requires MMU mode Qidibox (current: {Mmu.Mode}).");
        }

        Match match = Regex.Match(trimmed, pattern, RegexOptions.IgnoreCase);
        int numGates = Mmu.QidiboxBoxCount * 4;
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int slot) || slot < 0 || slot >= numGates)
        {
            throw new GcodeParameterException($"{commandShape}: slot index must be between 0 and {numGates - 1}.");
        }

        return slot;
    }

    // ---- AFC lane control macros ----
    // Consumed by PrintersController's mmu/gate-action endpoint (protocol=afc).
    private void ApplyAfcChangeToolLocked(string trimmed)
    {
        string lane = RequireAfcLaneName(trimmed, "CHANGE_TOOL");
        Mmu.AfcCurrentLoad = lane;
        Mmu.AfcCurrentState = "Idle";
    }

    private void ApplyAfcToolUnloadLocked(string trimmed)
    {
        string lane = RequireAfcLaneName(trimmed, "TOOL_UNLOAD");
        if (string.Equals(Mmu.AfcCurrentLoad, lane, StringComparison.Ordinal))
        {
            Mmu.AfcCurrentLoad = null;
        }

        Mmu.AfcCurrentState = "Idle";
    }

    private string RequireAfcLaneName(string trimmed, string command)
    {
        if (Mmu.Mode != MmuMode.Afc)
        {
            throw new GcodeParameterException($"{command} requires MMU mode Afc (current: {Mmu.Mode}).");
        }

        if (!TryParseStringParameter(trimmed, "LANE=", out string lane) ||
            !Mmu.LaneNames.Contains(lane, StringComparer.Ordinal))
        {
            throw new GcodeParameterException($"{command}: unknown AFC lane '{lane}'. Known lanes: {string.Join(", ", Mmu.LaneNames)}.");
        }

        return lane;
    }

    private static bool TryParseIntParameter(string line, string key, out int value)
    {
        value = 0;
        return TryExtractParameterToken(line, key, out string token) && int.TryParse(token, out value);
    }

    private static bool TryParseStringParameter(string line, string key, out string value)
    {
        return TryExtractParameterToken(line, key, out value);
    }

    private static bool TryExtractParameterToken(string line, string key, out string token)
    {
        token = string.Empty;
        int idx = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        int start = idx + key.Length;
        int end = start;
        while (end < line.Length && !char.IsWhiteSpace(line[end]))
        {
            end++;
        }

        token = line[start..end];
        return token.Length > 0;
    }

    private static double? ParseAxisValue(string line, char axis)
    {
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            line,
            $@"{axis}(-?\d+\.?\d*)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && double.TryParse(
            match.Groups[1].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double value)
            ? value
            : null;
    }

    private static bool TryParseSValue(string line, string prefix, out double value)
    {
        value = 0;
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            line,
            @"S(-?\d+\.?\d*)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && double.TryParse(
            match.Groups[1].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }

    private static string? ParseExcludeObjectName(string script)
    {
        int idx = script.IndexOf("NAME=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        string rest = script[(idx + 5)..].Trim();
        if (rest.StartsWith('"'))
        {
            bool escaped = false;
            for (int i = 1; i < rest.Length; i++)
            {
                if (rest[i] == '"' && !escaped)
                {
                    return rest[1..i].Replace("\\\"", "\"", StringComparison.Ordinal);
                }

                escaped = rest[i] == '\\' && !escaped;
                if (rest[i] != '\\')
                {
                    escaped = false;
                }
            }

            return null;
        }

        int space = rest.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? rest[..space] : rest;
    }

    /// <summary>Builds the full current Klipper object snapshot as plain, JSON-serializable dictionaries.</summary>
    public Dictionary<string, object> BuildObjectsSnapshot()
    {
        lock (_stateLock)
        {
            var objects = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["webhooks"] = new Dictionary<string, object?>
                {
                    ["state"] = KlippyState,
                    ["state_message"] = KlippyStateMessage,
                },
                ["print_stats"] = new Dictionary<string, object?>
                {
                    ["filename"] = Filename ?? string.Empty,
                    ["state"] = PrintState,
                    ["message"] = PrintMessage,
                    ["print_duration"] = PrintDuration,
                    ["total_duration"] = TotalDuration,
                    ["filament_used"] = FilamentUsed,
                    ["info"] = new Dictionary<string, object?>
                    {
                        ["total_layer"] = null,
                        ["current_layer"] = null,
                    },
                },
                ["toolhead"] = new Dictionary<string, object?>
                {
                    ["position"] = Position,
                    ["homed_axes"] = HomedAxes,

                    // Normally always "extruder" (single-hotend default). Snapmaker U1 mode
                    // reports whichever physical extruder is active instead ("extruder"/
                    // "extruderN"), matching SnapmakerU1PrintTaskConfigParser.ReadExtruderIndex.
                    ["extruder"] = Mmu.Mode == MmuMode.SnapmakerU1
                        ? (Mmu.SnapmakerU1ActiveToolheadIndex == 0 ? "extruder" : $"extruder{Mmu.SnapmakerU1ActiveToolheadIndex}")
                        : "extruder",
                    ["max_velocity"] = 300.0,
                    ["max_accel"] = 3000.0,
                },
                ["gcode_move"] = new Dictionary<string, object?>
                {
                    ["position"] = Position,
                    ["gcode_position"] = Position,
                    ["speed_factor"] = 1.0,
                    ["extrude_factor"] = 1.0,
                    ["absolute_coordinates"] = AbsoluteCoordinates,
                    ["homing_origin"] = new[] { 0.0, 0.0, 0.0, 0.0 },
                },
                ["extruder"] = new Dictionary<string, object?>
                {
                    ["temperature"] = ExtruderTemperature,
                    ["target"] = ExtruderTarget,
                    ["power"] = ExtruderTarget > 0 ? 0.4 : 0.0,
                },
                ["heater_bed"] = new Dictionary<string, object?>
                {
                    ["temperature"] = BedTemperature,
                    ["target"] = BedTarget,
                    ["power"] = BedTarget > 0 ? 0.3 : 0.0,
                },
                ["fan"] = new Dictionary<string, object?>
                {
                    ["speed"] = PrintState == "printing" ? 1.0 : 0.0,
                },
                ["virtual_sdcard"] = new Dictionary<string, object?>
                {
                    ["is_active"] = PrintState == "printing",
                    ["file_position"] = (long)(Progress() * 1_000_000),
                    ["file_size"] = 1_000_000L,
                    ["file_path"] = Filename,
                    ["progress"] = Progress(),
                },
                ["display_status"] = new Dictionary<string, object?>
                {
                    ["progress"] = Progress(),
                    ["message"] = PrintMessage,
                },
                ["exclude_object"] = new Dictionary<string, object?>
                {
                    ["objects"] = AvailableObjects.Select(name => new Dictionary<string, object?> { ["name"] = name }).ToArray(),
                    ["excluded_objects"] = ExcludedObjects.ToArray(),
                    ["current_object"] = CurrentObject,
                },
                ["motion_report"] = new Dictionary<string, object?>
                {
                    ["live_position"] = Position,
                    ["live_velocity"] = 0.0,
                    ["live_extruder_velocity"] = 0.0,
                },
                ["idle_timeout"] = new Dictionary<string, object?>
                {
                    ["state"] = PrintState == "printing" ? "Printing" : "Idle",
                },
            };

            if (Mmu.Mode == MmuMode.HappyHare)
            {
                // Wire keys must match MoonrakerSubscriptionService.HandleMmuUpdate exactly: it
                // reads "tool"/"gate" (Happy Hare's actual field names), not "active_tool"/
                // "active_gate" — using the latter silently drops every MMU update on the real
                // client.
                objects["mmu"] = new Dictionary<string, object?>
                {
                    ["enabled"] = Mmu.Enabled,
                    ["is_homed"] = Mmu.IsHomed,
                    ["tool"] = Mmu.ActiveTool,
                    ["gate"] = Mmu.ActiveGate,
                    ["filament"] = Mmu.FilamentState,
                    ["action"] = Mmu.Action,
                    ["num_gates"] = Mmu.NumGates,
                    ["has_bypass"] = Mmu.HasBypass,
                    ["endless_spool"] = Mmu.EndlessSpool,
                    ["clog_detection"] = Mmu.ClogDetection,
                    ["gate_status"] = Mmu.GateStatus,
                    ["gate_material"] = Mmu.GateMaterial,
                    ["gate_color"] = Mmu.GateColor,
                    ["gate_filament_name"] = Mmu.GateFilamentName,
                    ["gate_spool_id"] = Mmu.GateSpoolId,
                };
            }
            else if (Mmu.Mode == MmuMode.Afc)
            {
                // Matches MoonrakerSubscriptionService.HandleAfcUpdates: a top-level "AFC" object
                // (current_state/current_load/error_state/bypass_state/lanes) plus one
                // "AFC_stepper <lane>" object per lane (material/color/spool_id/load_state).
                objects["AFC"] = new Dictionary<string, object?>
                {
                    ["current_state"] = Mmu.AfcCurrentState,
                    ["current_load"] = Mmu.AfcCurrentLoad,
                    ["error_state"] = Mmu.AfcErrorState,
                    ["bypass_state"] = Mmu.AfcBypassState,
                    ["lanes"] = Mmu.LaneNames,
                };

                for (int i = 0; i < Mmu.LaneNames.Length; i++)
                {
                    objects[$"AFC_stepper {Mmu.LaneNames[i]}"] = new Dictionary<string, object?>
                    {
                        ["material"] = i < Mmu.GateMaterial.Length ? Mmu.GateMaterial[i] : null,
                        ["color"] = i < Mmu.GateColor.Length ? Mmu.GateColor[i] : null,
                        ["spool_id"] = i < Mmu.GateSpoolId.Length ? Mmu.GateSpoolId[i] : -1,
                        ["load_state"] = i < Mmu.GateStatus.Length && Mmu.GateStatus[i] == 1,
                    };
                }
            }
            else if (Mmu.Mode == MmuMode.Qidibox)
            {
                // Matches MoonrakerSubscriptionService.HandleQidiboxUpdatesAsync: a
                // "save_variables" object carrying box_count/last_load_slot plus per-slot
                // filament_slotN/color_slotN *dictionary codes* (resolved against
                // officiall_filas_list.cfg — see PrinterRegistry's seeded "config" root file and
                // GetConfigFileAsync), and one "box_stepper slotN" object per slot.
                var variables = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["box_count"] = Mmu.QidiboxBoxCount,
                    ["last_load_slot"] = Mmu.QidiboxLastLoadSlot,
                };

                for (int i = 0; i < Mmu.QidiboxFilamentTypeCodes.Length; i++)
                {
                    variables[$"filament_slot{i}"] = Mmu.QidiboxFilamentTypeCodes[i];
                }

                for (int i = 0; i < Mmu.QidiboxColorCodes.Length; i++)
                {
                    variables[$"color_slot{i}"] = Mmu.QidiboxColorCodes[i];
                }

                objects["save_variables"] = new Dictionary<string, object?> { ["variables"] = variables };

                for (int i = 0; i < Mmu.QidiboxRunoutButton.Length; i++)
                {
                    objects[$"box_stepper slot{i}"] = new Dictionary<string, object?>
                    {
                        ["runout_button"] = Mmu.QidiboxRunoutButton[i],
                    };
                }
            }
            else if (Mmu.Mode == MmuMode.SnapmakerU1)
            {
                // Matches SnapmakerU1PrintTaskConfigParser.ParseLaneDeltas: a "print_task_config"
                // object with parallel per-toolhead arrays. toolhead.extruder (set above) carries
                // the active toolhead index; this object carries per-toolhead filament state.
                objects["print_task_config"] = new Dictionary<string, object?>
                {
                    ["filament_exist"] = Mmu.SnapmakerU1FilamentExist,
                    ["filament_color_rgba"] = Mmu.SnapmakerU1FilamentColorRgba,
                    ["filament_type"] = Mmu.SnapmakerU1FilamentType,
                    ["filament_sub_type"] = Mmu.SnapmakerU1FilamentSubType,
                    ["filament_official"] = Mmu.SnapmakerU1FilamentOfficial,
                };
            }

            return objects;
        }
    }

    public void SetMmuMode(MmuMode mode)
    {
        lock (_stateLock)
        {
            Mmu.Mode = mode;
        }
    }
}
