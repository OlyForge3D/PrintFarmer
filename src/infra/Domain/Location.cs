using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class Location
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int PrinterCount { get; set; } = 0; // Denormalized count for efficient filtering

    // Tree structure
    public Guid? ParentId { get; set; }

    public Location? Parent { get; set; }

    public ICollection<Location> Children { get; } = new List<Location>();

    // Cached hierarchy
    public string Path { get; set; } = "/";

    public int Depth { get; set; } = 0;

    public int SortOrder { get; set; } = 0;

    public int TotalPrinterCount { get; set; } = 0; // Printers in this + all descendants

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    public bool IsActive { get; set; } = true;

    // Navigation property: all printers in this location
    public ICollection<Printer> Printers { get; } = new List<Printer>();
}
