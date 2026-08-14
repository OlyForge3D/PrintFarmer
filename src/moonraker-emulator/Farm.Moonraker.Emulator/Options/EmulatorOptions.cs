namespace Farm.Moonraker.Emulator.Options;

/// <summary>
/// Top-level configuration for the standalone Moonraker protocol emulator.
/// Bound from the "Emulator" configuration section.
///
/// One process instance always emulates exactly one printer, served at Moonraker's
/// normal root paths (<c>/printer/info</c>, <c>/websocket</c>, ...) — never behind a
/// path prefix or dispatched by Host header. This matches how the real backend plugin
/// talks to a printer: <c>Printer.BackendUrl</c> is stored with its trailing slash
/// trimmed and <c>MoonrakerClient</c> resolves every route as a *relative* URI against
/// it (<c>new Uri(baseUri, "printer/info")</c>), so whatever host:port a printer
/// record points at is always hit at the root. A single shared multi-tenant emulator
/// process could not honor a path prefix without changing that client, which is out of
/// scope. Instead, each seeded scenario (ready/printing/paused/shutdown) is meant to
/// run as its own isolated container/process, configured entirely through these three
/// settings — e.g. the "moonraker-printing" Compose service sets
/// <c>Emulator__Scenario=Printing</c>.
/// </summary>
public sealed class EmulatorOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Emulator";

    /// <summary>
    /// Whether the <c>/__emulator/**</c> test-control API is exposed. Disabled by
    /// default so the emulator behaves like a real Moonraker instance unless a test
    /// explicitly opts in.
    /// </summary>
    public bool EnableControlApi { get; set; }

    /// <summary>
    /// Which seed scenario this process instance emulates: "Ready", "Printing",
    /// "Paused", or "Shutdown" (case-insensitive). Defaults to "Ready".
    /// </summary>
    public string Scenario { get; set; } = "Ready";

    /// <summary>
    /// Stable identifier for this printer instance, used by the control API and by
    /// fault rules. Purely a local label — it never appears in the emulated Moonraker
    /// wire protocol.
    /// </summary>
    public string PrinterId { get; set; } = "printer";

    /// <summary>
    /// Display/host name for this printer instance. Reported verbatim as
    /// <c>printer/info</c>'s <c>hostname</c> field, so a Compose deployment should set
    /// it to the service's own hostname (e.g. "moonraker-ready") for discovery
    /// consistency.
    /// </summary>
    public string PrinterName { get; set; } = "Moonraker Emulator";

    /// <summary>
    /// Simulated seconds of virtual time to add per elapsed real second. Zero (the
    /// default) keeps the virtual clock fully deterministic: it only ever advances
    /// when <c>/__emulator/time/advance</c> is called or an action (e.g. starting a
    /// print) anchors a new baseline. Set greater than zero to let demo/dev stacks
    /// see live progress without polling the control API.
    /// </summary>
    public double TimeScale { get; set; }
}
