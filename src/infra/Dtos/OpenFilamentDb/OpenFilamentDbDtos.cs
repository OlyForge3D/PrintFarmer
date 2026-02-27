namespace Farm.Infrastructure.OpenFilamentDb;

/// <summary>
/// Response shape for the brands index endpoint.
/// GET /api/v1/brands/index.json
/// </summary>
public record OfdBrandsResponse
{
    public string Version { get; init; } = string.Empty;
    public int Count { get; init; }
    public List<OfdBrand> Brands { get; init; } = [];
}

/// <summary>
/// A filament brand/manufacturer from the Open Filament Database.
/// </summary>
public record OfdBrand
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string? Origin { get; init; }
    public int MaterialCount { get; init; }
    public string? Path { get; init; }
    public string? LogoSlug { get; init; }
}

/// <summary>
/// Response shape for a brand's detail page with materials.
/// GET /api/v1/brands/{slug}/index.json
/// </summary>
public record OfdBrandDetailResponse
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string? Website { get; init; }
    public string? Origin { get; init; }
    public List<OfdMaterialSummary> Materials { get; init; } = [];
}

/// <summary>
/// Summary of a material type within a brand.
/// </summary>
public record OfdMaterialSummary
{
    public string Id { get; init; } = string.Empty;
    public string Material { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public int FilamentCount { get; init; }
    public string? Path { get; init; }
}

/// <summary>
/// Response shape for a material detail page with filaments.
/// GET /api/v1/brands/{brand}/materials/{material}/index.json
/// </summary>
public record OfdMaterialDetailResponse
{
    public string Id { get; init; } = string.Empty;
    public string BrandId { get; init; } = string.Empty;
    public string Material { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public List<OfdFilamentSummary> Filaments { get; init; } = [];
}

/// <summary>
/// Summary of a filament within a material.
/// </summary>
public record OfdFilamentSummary
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public int VariantCount { get; init; }
    public string? Path { get; init; }
}

/// <summary>
/// Response shape for a filament detail page with variants.
/// GET /api/v1/brands/{brand}/materials/{material}/filaments/{filament}/index.json
/// </summary>
public record OfdFilamentDetailResponse
{
    public string Id { get; init; } = string.Empty;
    public string BrandId { get; init; } = string.Empty;
    public string MaterialId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string Material { get; init; } = string.Empty;
    public double? Density { get; init; }
    public double? DiameterTolerance { get; init; }
    public int? MinPrintTemperature { get; init; }
    public int? MaxPrintTemperature { get; init; }
    public int? MinBedTemperature { get; init; }
    public int? MaxBedTemperature { get; init; }
    public bool Discontinued { get; init; }
    public List<OfdVariantSummary> Variants { get; init; } = [];
}

/// <summary>
/// Summary of a color variant within a filament.
/// </summary>
public record OfdVariantSummary
{
    public string Id { get; init; } = string.Empty;
    public string ColorName { get; init; } = string.Empty;
    public string? ColorHex { get; init; }
    public string Slug { get; init; } = string.Empty;
    public int SizeCount { get; init; }
    public string? Path { get; init; }
}

/// <summary>
/// Full variant detail with sizes.
/// GET /api/v1/brands/{brand}/.../variants/{variant}.json
/// </summary>
public record OfdVariantDetailResponse
{
    public string Id { get; init; } = string.Empty;
    public string FilamentId { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string ColorName { get; init; } = string.Empty;
    public string? ColorHex { get; init; }
    public OfdTraits? Traits { get; init; }
    public bool Discontinued { get; init; }
    public List<OfdSize> Sizes { get; init; } = [];
}

/// <summary>
/// Traits describing special properties of a variant.
/// </summary>
public record OfdTraits
{
    public bool Translucent { get; init; }
    public bool Glow { get; init; }
    public bool Matte { get; init; }
    public bool Recycled { get; init; }
    public bool Recyclable { get; init; }
    public bool Biodegradable { get; init; }
}

/// <summary>
/// A specific size (weight + diameter) of a variant.
/// </summary>
public record OfdSize
{
    public string Id { get; init; } = string.Empty;
    public string VariantId { get; init; } = string.Empty;
    public double FilamentWeight { get; init; }
    public double Diameter { get; init; }
    public bool Discontinued { get; init; }
}

// ─── Flattened import entry (one per variant×size) ────────────────────────

/// <summary>
/// A flattened entry combining brand, filament, variant, and size data
/// for display in the browser modal and import processing.
/// </summary>
public record OfdFlattenedEntry
{
    /// <summary>Composite key: "{variantId}:{sizeId}" for unique identification.</summary>
    public string EntryId { get; init; } = string.Empty;

    public string BrandName { get; init; } = string.Empty;
    public string FilamentName { get; init; } = string.Empty;
    public string Material { get; init; } = string.Empty;
    public string ColorName { get; init; } = string.Empty;
    public string? ColorHex { get; init; }
    public double? Density { get; init; }
    public double Diameter { get; init; }
    public double Weight { get; init; }
    public int? MinPrintTemp { get; init; }
    public int? MaxPrintTemp { get; init; }
    public int? MinBedTemp { get; init; }
    public int? MaxBedTemp { get; init; }
    public bool Translucent { get; init; }
    public bool Glow { get; init; }
    public bool Matte { get; init; }
}

// ─── Import request/result ────────────────────────────────────────────────

/// <summary>
/// Request to import selected entries from the Open Filament Database.
/// </summary>
public record OfdImportRequest
{
    /// <summary>List of flattened entries to import.</summary>
    public List<OfdFlattenedEntry> Entries { get; init; } = [];
}

/// <summary>
/// Result of importing from the Open Filament Database.
/// </summary>
public record OfdImportResult(
    int CreatedCount,
    int UpdatedCount,
    int ErrorCount,
    string[] Errors);
