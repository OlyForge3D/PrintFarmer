using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Bounds ordinary manual commands without retaining operation receipts.</summary>
public static class PrinterDirectControl
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    public static bool IsManualMotion([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? operation) =>
        operation is "home" or "home_xy" or "home_z" or "move" or "move_to";

    /// <summary>
    /// Backends whose manual motion is preflighted against a fresh observed position and the
    /// verified travel envelope before dispatch. Relative jog is only advertised for these.
    /// </summary>
    public static bool HasObservedManualMotionPreflight(PrinterBackend backend) =>
        backend == PrinterBackend.Moonraker;
}
