namespace Farm.Web.Api.Infrastructure.Normalization;

/// <summary>
/// Provides consistent normalization for catalog display names.
/// Manufacturers: map to canonical brand stylizations when recognized (e.g. "flashforge" => "FlashForge").
/// Models: simple rule (trim + capitalize first character only) preserving remainder as-entered.
/// </summary>
internal static class CatalogNameNormalizer
{
    // Canonical manufacturer stylizations + common alias/spacing variants mapped to canonical.
    // Add new brands or aliases here to extend normalization coverage.
    private static readonly Dictionary<string, string> CanonicalManufacturerMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["prusa"] = "Prusa",
        ["prusa research"] = "Prusa",
        ["elegoo"] = "Elegoo",
        ["eryone"] = "Eryone",
        ["flashforge"] = "FlashForge",
        ["flash forge"] = "FlashForge",
        ["sovol"] = "Sovol",
        ["ratrig"] = "RatRig",
        ["rat rig"] = "RatRig",
        ["voron"] = "Voron",
        ["phrozen"] = "Phrozen",
        ["printersforants"] = "PrintersForAnts",
        ["printers for ants"] = "PrintersForAnts",
        ["esun"] = "eSun",
        ["e-sun"] = "eSun",
        ["anycubic"] = "Anycubic",
        ["bambulab"] = "BambuLab",
        ["bambu lab"] = "BambuLab"
    };

    /// <summary>
    /// Normalize a manufacturer name to its canonical stylization if known; otherwise fall back to simple first-letter capitalization.
    /// </summary>
    public static string NormalizeManufacturer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }
        string trimmed = name.Trim();
        if (CanonicalManufacturerMap.TryGetValue(trimmed, out string? canonical))
        {
            return canonical; // Known brand stylization
        }
        // Also try collapsing whitespace to catch spaced vs non-spaced variants
        string collapsed = string.Concat(trimmed.Where(c => !char.IsWhiteSpace(c)));
        return CanonicalManufacturerMap.TryGetValue(collapsed, out canonical) ? canonical : CapitalizeFirst(trimmed);
    }

    /// <summary>
    /// Normalize a model name (no canonical list; just capitalize first letter).
    /// </summary>
    public static string NormalizeModel(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? string.Empty : CapitalizeFirst(name.Trim());
    }

    /// <summary>
    /// Legacy generic normalization (kept for backward compatibility inside codebase). Prefer specific methods.
    /// </summary>
    public static string Normalize(string? name) => NormalizeModel(name);

    private static string CapitalizeFirst(string s) => char.ToUpperInvariant(s[0]) + (s.Length > 1 ? s[1..] : string.Empty);
}
