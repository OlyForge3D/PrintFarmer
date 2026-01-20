using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Maps slicer-specific printer model names to canonical PrinterModel entries.
/// For example, PrusaSlicer calls a model "COREONEL" while OrcaSlicer calls it "Prusa CORE One",
/// but both refer to the same physical printer in our catalog.
/// </summary>
public class PrinterModelAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The canonical PrinterModel this alias refers to
    /// </summary>
    public Guid PrinterModelId { get; set; }

    public PrinterModel? PrinterModel { get; set; }

    /// <summary>
    /// The slicer-specific name (e.g., "COREONEL", "Phrozen Arco", "Prusa CORE One")
    /// </summary>
    public string SlicerModelName { get; set; } = string.Empty;

    /// <summary>
    /// The slicer type (e.g., "PrusaSlicer", "OrcaSlicer", "Cura") - optional, if null applies to all slicers
    /// </summary>
    public string? SlicerType { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
