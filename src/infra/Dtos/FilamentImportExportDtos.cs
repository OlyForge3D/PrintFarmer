namespace Farm.Infrastructure;

/// <summary>
/// Result of importing filament types from a CSV file.
/// </summary>
/// <param name="CreatedCount">Number of new filament types created.</param>
/// <param name="UpdatedCount">Number of existing filament types updated.</param>
/// <param name="ErrorCount">Number of rows that failed to import.</param>
/// <param name="TotalRows">Total number of data rows in the CSV.</param>
/// <param name="Errors">Error messages for rows that failed.</param>
public record FilamentCsvImportResult(
    int CreatedCount,
    int UpdatedCount,
    int ErrorCount,
    int TotalRows,
    string[] Errors);

/// <summary>
/// Represents a single filament entry from SpoolmanDB (donkie/SpoolmanDB on GitHub).
/// Maps the JSON schema from https://donkie.github.io/SpoolmanDB/filaments.json
/// </summary>
public record SpoolmanDbFilamentEntry
{
    public string Id { get; init; } = string.Empty;

    public string Manufacturer { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Material { get; init; } = string.Empty;

    public double? Density { get; init; }

    public double? Weight { get; init; }

    public double? SpoolWeight { get; init; }

    public string? SpoolType { get; init; }

    public double? Diameter { get; init; }

    public string? ColorHex { get; init; }

    public string[]? ColorHexes { get; init; }

    public int? ExtruderTemp { get; init; }

    public int[]? ExtruderTempRange { get; init; }

    public int? BedTemp { get; init; }

    public int[]? BedTempRange { get; init; }

    public string? Finish { get; init; }

    public string? MultiColorDirection { get; init; }

    public string? Pattern { get; init; }

    public bool Translucent { get; init; }

    public bool Glow { get; init; }
}

/// <summary>
/// Represents a material entry from SpoolmanDB materials.json.
/// </summary>
public record SpoolmanDbMaterialEntry
{
    public string Material { get; init; } = string.Empty;

    public double? Density { get; init; }

    public int? ExtruderTemp { get; init; }

    public int? BedTemp { get; init; }
}

/// <summary>
/// Request to import selected filaments from SpoolmanDB.
/// </summary>
/// <param name="FilamentIds">Array of SpoolmanDB filament IDs to import.</param>
public record SpoolmanDbImportRequest(string[] FilamentIds);

/// <summary>
/// Result of importing filaments from SpoolmanDB.
/// </summary>
/// <param name="CreatedCount">Number of new filament types created.</param>
/// <param name="UpdatedCount">Number of existing filament types updated.</param>
/// <param name="ErrorCount">Number of filaments that failed to import.</param>
/// <param name="Errors">Error messages for entries that failed.</param>
public record SpoolmanDbImportResult(
    int CreatedCount,
    int UpdatedCount,
    int ErrorCount,
    string[] Errors);
