using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure.Discovery;

namespace Farm.Infrastructure;

#pragma warning restore CA1056

/// <summary>
/// Location DTO for reading and listing printer locations.
/// Contains all location properties including hierarchy and associated printer count.
/// </summary>
public class LocationDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int PrinterCount { get; set; }

    public Guid? ParentId { get; set; }

    public string Path { get; set; } = "/";

    public int Depth { get; set; }

    public int SortOrder { get; set; }

    public int TotalPrinterCount { get; set; }

    public List<LocationDto>? Children { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ModifiedAt { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// DTO for creating a new printer location.
/// Includes required and optional location information.
/// </summary>
public class CreateLocationDto
{
    [Required(ErrorMessage = "Location name is required.")]
    [StringLength(256, MinimumLength = 1, ErrorMessage = "Location name must be between 1 and 256 characters.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(1024, ErrorMessage = "Location description cannot exceed 1024 characters.")]
    public string? Description { get; set; }

    public Guid? ParentId { get; set; }

    public int? SortOrder { get; set; }
}

/// <summary>
/// DTO for updating an existing printer location.
/// Allows updating location name, description, and hierarchy position.
/// </summary>
public class UpdateLocationDto
{
    [StringLength(256, MinimumLength = 1, ErrorMessage = "Location name must be between 1 and 256 characters.")]
    public string? Name { get; set; }

    [StringLength(1024, ErrorMessage = "Location description cannot exceed 1024 characters.")]
    public string? Description { get; set; }

    public Guid? ParentId { get; set; }

    public int? SortOrder { get; set; }
}

/// <summary>
/// Lightweight location summary DTO for inclusion in printer list responses.
/// Contains essential location information for display purposes.
/// </summary>
public record LocationSummaryDto(
    Guid Id,
    string Name,
    string? Description);

/// <summary>
/// Location details DTO including associated printers.
/// Used when retrieving a location with its full printer list.
/// </summary>
public class LocationDetailsDto : LocationDto
{
    public DiscoveryPrinterInfoDto[] Printers { get; set; } = [];
}

/// <summary>
/// Nested tree structure DTO for location hierarchy display.
/// </summary>
public class LocationTreeDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Guid? ParentId { get; set; }

    public string Path { get; set; } = "/";

    public int Depth { get; set; }

    public int SortOrder { get; set; }

    public int PrinterCount { get; set; }

    public int TotalPrinterCount { get; set; }

    public List<LocationTreeDto> Children { get; set; } = [];
}

/// <summary>
/// Breadcrumb DTO for displaying location ancestor chain.
/// </summary>
public record LocationBreadcrumbDto(Guid Id, string Name);

/// <summary>
/// DTO for moving a location to a new parent.
/// </summary>
public class MoveLocationDto
{
    public Guid? NewParentId { get; set; }
}
