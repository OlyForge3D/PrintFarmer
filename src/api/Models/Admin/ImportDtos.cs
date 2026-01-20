using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Models.Admin;

/// <summary>
/// Import mode for catalog and full backup imports
/// </summary>
public enum ImportMode
{
    /// <summary>
    /// Merge imported data with existing data. Skips duplicates (by name).
    /// </summary>
    Merge = 0,

    /// <summary>
    /// Replace all existing data with imported data. WARNING: Deletes all existing catalog data first!
    /// </summary>
    Replace = 1
}

/// <summary>
/// Request to import catalog data (manufacturers, models, components)
/// </summary>
public class CatalogImportRequest
{
    [Required]
    public CatalogExportDto Catalog { get; set; } = new();

    public ImportMode Mode { get; set; } = ImportMode.Merge;
}

/// <summary>
/// Request to import full backup (catalog + printers + locations)
/// </summary>
public class FullBackupImportRequest
{
    [Required]
    public FullBackupExportDto Backup { get; set; } = new();

    public ImportMode Mode { get; set; } = ImportMode.Merge;
}

/// <summary>
/// Response from import operation with progress and status details
/// </summary>
public class ImportResponseDto
{
    public bool Success { get; set; }

    public List<string> Errors { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    public ImportStatistics Statistics { get; set; } = new();

    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Import statistics showing counts of imported items
/// </summary>
public class ImportStatistics
{
    public int ManufacturersImported { get; set; }

    public int FilamentTypesImported { get; set; }

    public int PrinterModelsImported { get; set; }

    public int HotendsImported { get; set; }

    public int ExtrudersImported { get; set; }

    public int ToolheadsImported { get; set; }

    public int NozzlesImported { get; set; }

    public int PrintersImported { get; set; }

    public int LocationsImported { get; set; }

    public int TotalItemsImported { get; set; }

    public TimeSpan Duration { get; set; }
}
