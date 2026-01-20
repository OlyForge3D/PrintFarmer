namespace Farm.Infrastructure;

public class BulkProfileImportRequest
{
    public List<Guid>? ProfileIds { get; set; }

    public bool? MakePublic { get; set; }
}

public class BulkProfileImportResultDto
{
    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public int TotalRequested { get; set; }

    public int TotalFound { get; set; }

    public int Imported { get; set; }

    public int Duplicated { get; set; }
}

/// <summary>
/// Request to import profiles directly from the OrcaSlicer worker (not from pre-seeded database).
/// Used when profiles haven't been seeded yet and come directly from the worker.
/// </summary>
public class BulkImportFromWorkerRequest
{
    /// <summary>
    /// Profiles to import, as returned from the OrcaSlicer worker (/profiles endpoint)
    /// </summary>
    public List<SlicerProfileDto>? Profiles { get; set; }

    public bool? MakePublic { get; set; }
}

public class BulkImportFromWorkerResultDto
{
    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public int Imported { get; set; }

    public int Duplicated { get; set; }
}
