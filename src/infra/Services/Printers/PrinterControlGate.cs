using System.Collections.Generic;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Server-side gate for printer control commands. Decides whether a printer's
/// last-known state forbids commands like /temps, /move, and /moveto.
/// </summary>
public static class PrinterControlGate
{
    private static readonly HashSet<string> BusyStates = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "Printing",
        "Pausing",
        "Paused",
        "Resuming",
        "Cancelling",
        "Heating",
    };

    public static bool IsBusyForControl(string? state)
        => !string.IsNullOrWhiteSpace(state) && BusyStates.Contains(state.Trim());
}
