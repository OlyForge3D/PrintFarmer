using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Derives and applies whether a printer can attribute wear to individual physical toolheads.
/// </summary>
public static class PerToolAttributionCapability
{
    /// <summary>
    /// Re-derives the capability from backend and physical topology, changing the entity only when
    /// the derived value differs from the persisted flag.
    /// </summary>
    /// <returns><c>true</c> when the flag changed; otherwise <c>false</c>.</returns>
    public static bool Refresh(Printer printer)
    {
        ArgumentNullException.ThrowIfNull(printer);
        bool derivedSupport = IsSupported((PrinterBackend)printer.Backend, printer.Toolheads);
        if (printer.SupportsPerToolAttribution == derivedSupport)
        {
            return false;
        }

        printer.SupportsPerToolAttribution = derivedSupport;
        return true;
    }

    /// <summary>
    /// Determines whether a backend and topology provide genuine interval-aware per-tool telemetry.
    /// </summary>
    public static bool IsSupported(PrinterBackend backend, IEnumerable<Toolhead> toolheads)
    {
        ArgumentNullException.ThrowIfNull(toolheads);
        return backend == PrinterBackend.Moonraker
            && toolheads.Count(toolhead => toolhead.ToolheadType == ToolheadType.Physical) >= 2;
    }
}
