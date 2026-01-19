using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class Manufacturer
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Url { get; set; }

    public string? Description { get; set; }

    public ICollection<PrinterModel> Models { get; } = new List<PrinterModel>();

    public bool IsActive { get; set; } = true;
}
