namespace Farm.Infrastructure.Services.Printers;

/// <summary>One source tool slot. Index is zero-based; SourceId preserves a native identifier if provided.</summary>
public sealed record PrinterFilamentTool
{
    public int Index { get; init; }

    public string? SourceId { get; init; }

    public string? Material { get; init; }

    public string? Color { get; init; }

    public double? FilamentUsedG { get; init; }

    /// <summary>Rejects unsafe text, invalid indices/colors and invented non-finite weights.</summary>
    public bool IsValid() =>
        Index is >= 0 and < PrinterPrintMetadata.MaximumTools &&
        (SourceId is null || PrinterPrintMetadata.IsValidText(SourceId)) &&
        (Material is null || PrinterPrintMetadata.IsValidText(Material)) &&
        (Color is null || IsValidColor(Color)) &&
        (FilamentUsedG is null || PrinterPrintMetadata.IsValidNumber(FilamentUsedG.Value));

    /// <summary>Only literal RGB/RGBA colors are accepted, never CSS expressions.</summary>
    public static bool IsValidColor(string? color) =>
        color is { Length: 7 or 9 } && color[0] == '#' && color.AsSpan(1).IndexOfAnyExcept(
            "0123456789abcdefABCDEF".AsSpan()) < 0;
}
