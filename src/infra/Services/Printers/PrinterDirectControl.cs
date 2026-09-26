namespace Farm.Infrastructure.Services.Printers;

/// <summary>Bounds ordinary manual commands without retaining operation receipts.</summary>
public static class PrinterDirectControl
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    public static bool IsManualMotion([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? operation) =>
        operation is "home" or "home_xy" or "home_z" or "move" or "move_to";
}
