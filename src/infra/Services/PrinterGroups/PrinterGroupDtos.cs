namespace Farm.Infrastructure.Services.PrinterGroups;

/// <summary>
/// DTOs for the PrinterGroup API endpoints.
/// </summary>
public record PrinterGroupDto
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public DateTimeOffset CreatedDate { get; init; }

    public DateTimeOffset UpdatedDate { get; init; }

    public int PrinterCount { get; init; }
}

public record PrinterGroupDetailDto
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public DateTimeOffset CreatedDate { get; init; }

    public DateTimeOffset UpdatedDate { get; init; }

    public IReadOnlyList<PrinterGroupPrinterDto> Printers { get; init; } = [];
}

public record PrinterGroupPrinterDto
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public int Backend { get; init; }

    public bool IsAvailable { get; init; }

    public bool InMaintenance { get; init; }
}

public record CreatePrinterGroupDto
{
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }
}

public record UpdatePrinterGroupDto
{
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }
}
