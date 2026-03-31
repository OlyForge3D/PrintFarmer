namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Utility for normalizing printer state strings to a canonical set for consistent UI display.
/// Backends use different terminology for equivalent states — this maps them all to a small,
/// well-defined set: Idle, Printing, Paused, Error, Offline, Shutdown, Halted, Disconnected, Complete, Cancelled.
/// </summary>
public static class PrinterStateNormalizer
{
    /// <summary>
    /// Maps backend-specific state strings to canonical states.
    /// Key: lowercased backend value. Value: canonical display state.
    /// </summary>
    private static readonly Dictionary<string, string> StateMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Idle-equivalent states (printer is ready to accept a job)
        ["idle"] = "Idle",
        ["standby"] = "Idle",
        ["ready"] = "Idle",
        ["operational"] = "Idle",
        ["online"] = "Idle",

        // Printing states
        ["printing"] = "Printing",
        ["building"] = "Printing",
        ["busy"] = "Printing",
        ["preparing"] = "Printing",
        ["starting"] = "Printing",

        // Paused states
        ["paused"] = "Paused",

        // Completed states
        ["complete"] = "Complete",
        ["finished"] = "Complete",
        ["stopped"] = "Complete",

        // Cancelled states
        ["cancelled"] = "Cancelled",

        // Error/attention states
        ["error"] = "Error",
        ["attention"] = "Error",

        // Offline states
        ["offline"] = "Offline",

        // Shutdown/halted/disconnected — kept as distinct states
        ["shutdown"] = "Shutdown",
        ["halted"] = "Halted",
        ["disconnected"] = "Disconnected",

        // Connecting states
        ["connecting"] = "Connecting",

        // Unknown
        ["unknown"] = "Idle",
    };

    /// <summary>
    /// Normalize a printer state string to a canonical display value.
    /// First checks the semantic mapping table, then falls back to PascalCase conversion
    /// for any unrecognized states.
    /// </summary>
    public static string? NormalizeState(string? state)
    {
        if (string.IsNullOrEmpty(state))
        {
            return state;
        }

        // Try semantic mapping first
        if (StateMap.TryGetValue(state.Trim(), out string? canonical))
        {
            return canonical;
        }

        // Fallback: PascalCase for any unrecognized state so it at least looks clean
        string lower = state.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
    }
}
