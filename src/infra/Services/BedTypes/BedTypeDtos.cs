namespace Farm.Infrastructure.Services.BedTypes;

/// <summary>
/// DTOs for the BedType API endpoints.
/// </summary>
public record BedTypeDto
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public bool IsSystem { get; init; }

    public string? Color { get; init; }

    public DateTimeOffset CreatedDate { get; init; }

    public DateTimeOffset UpdatedDate { get; init; }

    public int PrinterCount { get; init; }
}

public record CreateBedTypeDto
{
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? Color { get; init; }
}

public record UpdateBedTypeDto
{
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? Color { get; init; }
}
