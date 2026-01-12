namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Utility for normalizing printer state strings to PascalCase for consistent UI display.
/// </summary>
public static class PrinterStateNormalizer
{
    /// <summary>
    /// Normalize a printer state string to PascalCase.
    /// Examples: "IDLE" → "Idle", "printing" → "Printing", "Paused" → "Paused"
    /// </summary>
    public static string? NormalizeState(string? state)
    {
        if (string.IsNullOrEmpty(state))
        {
            return state;
        }

        // Convert to lowercase first, then capitalize first letter
        var lower = state.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
    }
}
