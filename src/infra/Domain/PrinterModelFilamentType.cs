using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class PrinterModelFilamentType
{
    public Guid PrinterModelId { get; set; }

    public PrinterModel? PrinterModel { get; set; }

    public Guid FilamentTypeId { get; set; }

    public FilamentType? FilamentType { get; set; }
}
