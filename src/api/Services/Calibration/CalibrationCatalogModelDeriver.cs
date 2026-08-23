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
    /// <summary>
    /// The reserved catalog model name every printer's non-nullable <c>ModelId</c> resolves to
    /// by default when no real catalog association has been made (see <c>Printer.ModelId</c>'s
    /// "No longer nullable - uses default Unknown Model" comment, and
    /// <c>EfCatalogRepository.GetUnknownModelIdAsync</c>'s identical Name check). This row is
    /// seeded with plausible generic placeholder values (a 200x200x200 build volume, a stock
    /// toolhead, <c>hasHeatedBed: true</c>, etc.) so it renders sensibly elsewhere in the
    /// product, but none of that was curated for any specific printer -- it must never be
    /// surfaced as a derived calibration fact, or every un-cataloged printer would silently
    /// inherit fabricated hardware data it was never verified against (#1922). Exposed so the
    /// caller can resolve the sentinel's actual <see cref="PrinterModel.Id"/> (scoped to the
    /// "Unknown" manufacturer, not by name alone -- <see cref="PrinterModel"/> only enforces
    /// uniqueness on <c>(ManufacturerId, Name)</c>, so a different manufacturer could otherwise
    /// legitimately curate a model that happens to share this name).
    /// </summary>
    internal const string UnknownModelName = "Unknown Model";

    /// <summary>
    /// Derives calibration hardware facts from <paramref name="model"/>, unless it is the
    /// reserved "Unknown Model" sentinel (identified by <paramref name="unknownModelId"/>), in
    /// which case its facts are never surfaced (#1922).
    /// </summary>
    /// <param name="model">The printer's linked catalog model, if any.</param>
    /// <param name="unknownModelId">
    /// The resolved id of the reserved "Unknown Model" sentinel row (see
    /// <see cref="UnknownModelName"/>), or <see langword="null"/> if that row could not be
    /// resolved (e.g. an environment with no "Unknown" manufacturer seeded).
    /// </param>
    public static DerivedCatalogFacts Derive(PrinterModel? model, Guid? unknownModelId)
    {
        if (model is null || (unknownModelId is not null && model.Id == unknownModelId))
        {
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
