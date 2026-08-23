using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;

namespace Farm.Web.Api.Services.Calibration;

/// <summary>
/// Derives calibration hardware facts from the printer's catalog model (<see cref="PrinterModel"/>,
/// <see cref="PrinterModelToolhead"/>, and component definitions), used as the third and final
/// fallback tier in <c>coalesce(printer override, machine-profile derived, catalog derived)</c>
/// (#1922):
/// <code>
/// printer.X ?? machineProfile.X ?? catalogModel.X ?? null
/// </code>
/// A <see langword="null"/> field means the catalog does not assert that fact either, so it is
/// genuinely missing and must be reported via <c>missingInputs</c>.
/// </summary>
internal static class CalibrationCatalogModelDeriver
{
    public static DerivedCatalogFacts Derive(PrinterModel? model)
    {
        if (model is null || !HasCuratedData(model))
        {
            // A model with no curated geometry, motion, acceleration, or toolhead data (e.g. the
            // generic catalog placeholder every printer's non-nullable ModelId resolves to by
            // default -- see Printer.ModelId) asserts nothing meaningful. Surfacing its bare
            // HasHeatedBed/HasEnclosure/HasHeatedChamber bool defaults as derived "facts" in that
            // case would fabricate data nobody entered, contradicting #1922's requirement that
            // missingInputs report only what is genuinely underivable.
            return DerivedCatalogFacts.Empty;
        }

        bool hasBuildPlane = model.MaxX is > 0 && model.MaxY is > 0;
        IReadOnlyList<CalibrationPointDto>? printablePolygon = hasBuildPlane
            ? new CalibrationPointDto[]
            {
                new(0, 0),
                new(model.MaxX!.Value, 0),
                new(model.MaxX.Value, model.MaxY!.Value),
                new(0, model.MaxY.Value),
            }
            : null;
        double? bedOriginX = hasBuildPlane ? 0 : null;
        double? bedOriginY = hasBuildPlane ? 0 : null;

        CalibrationMotionType? motionType = model.MotionType.HasValue
            ? (CalibrationMotionType)model.MotionType.Value
            : null;

        int? activeToolheadIndex = model.Toolheads
            .Where(toolhead => toolhead.IsPrimary)
            .Select(toolhead => (int?)toolhead.Index)
            .FirstOrDefault();

        Dictionary<int, CatalogToolheadFacts> toolheadsByIndex = model.Toolheads
            .GroupBy(toolhead => toolhead.Index)
            .ToDictionary(group => group.Key, group => DeriveToolhead(model, group.First()));

        return new DerivedCatalogFacts(
            printablePolygon,
            bedOriginX,
            bedOriginY,
            model.MaxX,
            model.MaxY,
            model.MaxZ,
            motionType,
            model.MaxAcceleration,
            model.MaxTravelAcceleration,
            model.HasHeatedBed,
            model.HasHeatedChamber,
            model.HasEnclosure,
            activeToolheadIndex,
            toolheadsByIndex);
    }

    /// <summary>
    /// A catalog model counts as curated -- and therefore eligible to contribute its
    /// bool-valued facts (HasHeatedBed, HasHeatedChamber, HasEnclosure), which have no "unset"
    /// representation of their own -- only once it asserts at least one piece of real hardware
    /// data: motion type, build geometry, acceleration limits, or a linked toolhead.
    /// </summary>
    private static bool HasCuratedData(PrinterModel model) =>
        model.MotionType is not null ||
        model.MaxX is not null ||
        model.MaxY is not null ||
        model.MaxZ is not null ||
        model.MaxAcceleration is not null ||
        model.MaxTravelAcceleration is not null ||
        model.Toolheads.Count > 0;

    private static CatalogToolheadFacts DeriveToolhead(PrinterModel model, PrinterModelToolhead toolhead)
    {
        NozzleModelDefinition? nozzle = toolhead.NozzleModel;
        HotendModelDefinition? hotend = toolhead.HotendModel;
        ExtruderModelDefinition? extruder = toolhead.ExtruderModel;

        // Prefer the linked extruder model's own drive type; fall back to the printer model's
        // bowden-tube flag only when it positively asserts a bowden setup (#1922).
        string? driveType = extruder is not null
            ? extruder.IsDirectDrive ? "direct" : "bowden"
            : model.HasBowdenTube ? "bowden" : null;

        return new CatalogToolheadFacts(
            nozzle?.Diameter,
            nozzle?.NozzleType,
            nozzle?.NozzleMaterial?.Name,
            nozzle?.IsHardened,
            nozzle?.MaxTemp,
            hotend?.MaxTemp,
            hotend?.MaxFlowRate,
            driveType,
            extruder?.IsDirectDrive,
            extruder?.GearRatio,
            toolhead.SupportedMaterials);
    }
}

/// <summary>
/// Facts derivable from the printer's catalog model, used as the final fallback source in
/// <c>coalesce(printer override, machine-profile derived, catalog derived)</c> (#1922). A
/// <see langword="null"/> field means the catalog does not assert that fact either.
/// </summary>
internal readonly record struct DerivedCatalogFacts(
    IReadOnlyList<CalibrationPointDto>? PrintablePolygon,
    double? BedOriginX,
    double? BedOriginY,
    double? BuildVolumeX,
    double? BuildVolumeY,
    double? BuildVolumeZ,
    CalibrationMotionType? MotionType,
    int? MaxAcceleration,
    int? MaxTravelAcceleration,
    bool? HasHeatedBed,
    bool? HasHeatedChamber,
    bool? HasEnclosure,
    int? ActiveToolheadIndex,
    IReadOnlyDictionary<int, CatalogToolheadFacts> ToolheadsByIndex)
{
    public static DerivedCatalogFacts Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null,
        new Dictionary<int, CatalogToolheadFacts>());
}

/// <summary>
/// Per-toolhead-index facts derived from <see cref="PrinterModelToolhead"/> and its linked
/// component definitions (#1922).
/// </summary>
internal readonly record struct CatalogToolheadFacts(
    double? NozzleDiameter,
    NozzleType? NozzleType,
    string? NozzleMaterial,
    bool? NozzleIsHardened,
    int? NozzleMaxTemperature,
    int? HotendMaxTemperature,
    double? MaxVolumetricFlow,
    string? DriveType,
    bool? IsDirectDrive,
    string? ExtruderGearRatio,
    IReadOnlyList<string>? SupportedMaterials);
