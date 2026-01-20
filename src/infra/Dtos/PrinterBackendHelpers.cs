using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Utility helpers for printer backend operations.
/// </summary>
public static class PrinterBackendHelpers
{
    /// <summary>
    /// Gets the default backend port for a given printer backend.
    /// Moonraker uses 7125, all other backends use 80.
    /// </summary>
    public static int GetDefaultPort(PrinterBackend backend) =>
        backend == PrinterBackend.Moonraker ? 7125 : 80;
}
