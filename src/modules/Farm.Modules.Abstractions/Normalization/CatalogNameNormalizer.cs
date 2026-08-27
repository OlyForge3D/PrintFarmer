namespace Farm.Modules.Abstractions.Normalization;

/// <summary>
/// Provides consistent normalization for catalog display names.
/// Manufacturers: map to canonical brand stylizations when recognized (e.g. "flashforge" => "FlashForge").
/// Models: simple rule (trim + capitalize first character only) preserving remainder as-entered.
/// </summary>
/// <remarks>
/// This is the single canonical <c>CatalogNameNormalizer</c> implementation. A former third copy
/// in <c>Farm.Web.Api.Infrastructure.Normalization</c> was pure redundancy (the web API project
/// already references <c>Farm.Infrastructure</c>) and was deleted as part of #2080 (N-DUP-1). A
/// second copy lived in <c>Farm.Infrastructure.Normalization</c>; #2100 deleted it and repointed
/// its call sites here, since <c>Farm.Modules.Abstractions</c> is a dependency-graph leaf (zero
/// project references beyond <c>Microsoft.AspNetCore.App</c>) and <c>Farm.Infrastructure</c>
/// referencing it introduces no cycle.
/// </remarks>
public static class CatalogNameNormalizer
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
        ["bambu lab"] = "BambuLab",
    };

    /// <summary>
    /// Normalize a manufacturer name to its canonical stylization if known; otherwise fall back
    /// to simple first-letter capitalization.
    /// </summary>
    /// <param name="name">The manufacturer name to normalize.</param>
    /// <returns>The normalized manufacturer name.</returns>
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
    /// <param name="name">The model name to normalize.</param>
    public static string NormalizeModel(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? string.Empty : CapitalizeFirst(name.Trim());
    }

    /// <summary>
    /// Generic normalization (kept for API parity with the existing monolith normalizer). Prefer
    /// the specific <see cref="NormalizeManufacturer"/>/<see cref="NormalizeModel"/> methods.
    /// </summary>
    /// <param name="name">The name to normalize.</param>
    public static string Normalize(string? name) => NormalizeModel(name);

    private static string CapitalizeFirst(string s) => char.ToUpperInvariant(s[0]) + (s.Length > 1 ? s[1..] : string.Empty);
}
