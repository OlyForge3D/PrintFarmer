using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure;

#pragma warning restore CA1056

/// <summary>
/// Location DTO for reading and listing printer locations.
/// Contains all location properties including associated printer count.
/// </summary>
public class LocationDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int PrinterCount { get; set; }

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
}

/// <summary>
/// DTO for updating an existing printer location.
/// Allows updating location name, description, and address.
/// </summary>
public class UpdateLocationDto
{
    [StringLength(256, MinimumLength = 1, ErrorMessage = "Location name must be between 1 and 256 characters.")]
    public string? Name { get; set; }

    [StringLength(1024, ErrorMessage = "Location description cannot exceed 1024 characters.")]
    public string? Description { get; set; }
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
    public PrinterInfoDto[] Printers { get; set; } = [];
}
