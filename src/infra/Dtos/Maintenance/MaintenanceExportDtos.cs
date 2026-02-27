namespace Farm.Infrastructure.Dtos.Maintenance;

/// <summary>
/// Result of a maintenance JSON import operation.
/// </summary>
public record MaintenanceImportResult(
    int CreatedCount,
    int UpdatedCount,
    int ErrorCount,
    string[] Errors,
    string[] Warnings);

// ── Export envelope ──────────────────────────────────────────

/// <summary>
/// Top-level envelope for all maintenance JSON exports.
/// The <see cref="ExportType"/> discriminator indicates which collections are populated.
/// </summary>
public class MaintenanceExportEnvelope
{
    public int Version { get; set; } = 1;
    public string ExportType { get; set; } = string.Empty;
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;

    public List<ComponentExportDto>? Components { get; set; }
    public List<TaskExportDto>? Tasks { get; set; }
    public List<PlanExportDto>? Plans { get; set; }
}

// ── Component (Spare Part) ──────────────────────────────────

public class ComponentExportDto
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string? Description { get; set; }
    public decimal? UnitCost { get; set; }
    public string? Supplier { get; set; }
    public string? Url { get; set; }
    public int InStock { get; set; }
    public int MinimumStock { get; set; }
}

// ── Task ────────────────────────────────────────────────────

public class TaskExportDto
{
    public string TaskName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Description { get; set; }
    public double? IntervalHours { get; set; }
    public int? IntervalDays { get; set; }
    public int? EstimatedDurationMinutes { get; set; }
    public int Priority { get; set; } = 2;
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Components required by this task, referenced by name for portability.
    /// </summary>
    public List<TaskComponentRefDto>? Components { get; set; }
}

public class TaskComponentRefDto
{
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public string? Notes { get; set; }
}

// ── Plan ────────────────────────────────────────────────────

public class PlanExportDto
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Tasks included in this plan, referenced by task name for portability.
    /// </summary>
    public List<PlanTaskRefDto>? Tasks { get; set; }
}

public class PlanTaskRefDto
{
    public string TaskName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public double? IntervalHoursOverride { get; set; }
    public int? IntervalDaysOverride { get; set; }
}
