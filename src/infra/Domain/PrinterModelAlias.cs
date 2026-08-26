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
    private string _slicerModelName = string.Empty;
    private string? _slicerType;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The canonical PrinterModel this alias refers to
    /// </summary>
    public Guid PrinterModelId { get; set; }

    public PrinterModel? PrinterModel { get; set; }

    /// <summary>
    /// The slicer-specific name (e.g., "COREONEL", "Phrozen Arco", "Prusa CORE One")
    /// </summary>
    public string SlicerModelName
    {
        get => _slicerModelName;
        set
        {
            _slicerModelName = value ?? string.Empty;
            SlicerModelNameNormalized = NormalizeLookupValue(_slicerModelName);
        }
    }

    /// <summary>
    /// Trimmed, case-folded alias name used by the indexed lookup path.
    /// </summary>
    public string SlicerModelNameNormalized { get; private set; } = string.Empty;

    /// <summary>
    /// The slicer type (e.g., "PrusaSlicer", "OrcaSlicer", "Cura") - optional, if null applies to all slicers
    /// </summary>
    public string? SlicerType
    {
        get => _slicerType;
        set
        {
            _slicerType = value;
            SlicerTypeNormalized = value is null ? null : NormalizeLookupValue(value);
        }
    }

    /// <summary>
    /// Trimmed, case-folded slicer type used by the indexed lookup path.
    /// </summary>
    public string? SlicerTypeNormalized { get; private set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    internal static string NormalizeLookupValue(string value) =>
        value.Trim().ToUpperInvariant();
}
