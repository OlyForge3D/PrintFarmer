using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class FilamentType
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public double? DefaultHotendTemp { get; set; }

    public double? DefaultBedTemp { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PrinterModelFilamentType> PrinterModels { get; } = new List<PrinterModelFilamentType>();

    public bool IsActive { get; set; } = true;
}
