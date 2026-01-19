namespace Farm.Infrastructure;

/// <summary>
/// Toolhead type - stock vs aftermarket/custom.
/// </summary>
public enum ToolheadType
{
    /// <summary>Stock/original toolhead from manufacturer.</summary>
    Stock = 0,

    /// <summary>Aftermarket or custom toolhead.</summary>
    Custom = 1
}
