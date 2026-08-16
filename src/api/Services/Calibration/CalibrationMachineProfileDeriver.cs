using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;

namespace Farm.Web.Api.Services.Calibration;

/// <summary>
/// Adapts the typed derived fields already populated on a resolved OrcaSlicer machine profile
/// (see <see cref="ResolvedCalibrationProfile"/>, #1613 §4.3) into the internal
/// <see cref="DerivedMachineFacts"/> shape used as the profile-derived fallback source in
/// <c>coalesce(explicit override, profile-derived value)</c> per #1613 §4.2.
///
/// As of #1615 (PR-2) this is a pure typed-field passthrough: parsing the raw OrcaSlicer profile
/// JSON now happens once, producer-side, in <c>Farm.Slicer.Module</c>'s
/// <c>CalibrationProfileResolver</c> (via the shared <c>Farm.Slicer.ProfileParsing</c> library),
/// so <c>src/api</c> no longer understands the OrcaSlicer profile-JSON shape at all &#8212;
/// preserving its zero-dependency boundary against <c>orcaslicer-worker</c>.
/// </summary>
internal static class CalibrationMachineProfileDeriver
{
    public static DerivedMachineFacts Derive(ResolvedCalibrationProfile? machine)
    {
        if (machine is null)
        {
            return DerivedMachineFacts.Empty;
        }

        return new DerivedMachineFacts(
            machine.PrintablePolygon,
            machine.BedOriginX,
            machine.BedOriginY,
            machine.BuildVolumeX,
            machine.BuildVolumeY,
            machine.BuildVolumeZ,
            machine.MotionType,
            machine.MaxAcceleration,
            machine.MaxTravelSpeed,
            machine.HasHeatedBed,
            machine.HasHeatedChamber,
            machine.NozzleDiameter,
            machine.NozzleType,
            machine.NozzleMaxTemperature,
            machine.HotendMaxTemperature);
    }
}

/// <summary>
/// Facts derivable from a resolved OrcaSlicer machine profile, used as the profile-derived
/// fallback source in <c>coalesce(explicit override, profile-derived value)</c> per #1613 §4.2.
/// A <see langword="null"/> field means the profile did not assert that fact.
/// </summary>
internal readonly record struct DerivedMachineFacts(
    IReadOnlyList<CalibrationPointDto>? PrintablePolygon,
    double? BedOriginX,
    double? BedOriginY,
    double? BuildVolumeX,
    double? BuildVolumeY,
    double? BuildVolumeZ,
    CalibrationMotionType? MotionType,
    int? MaxAcceleration,
    int? MaxTravelSpeed,
    bool? HasHeatedBed,
    bool? HasHeatedChamber,
    double? NozzleDiameter,
    NozzleType? NozzleType,
    int? NozzleMaxTemperature,
    int? HotendMaxTemperature)
{
    public static DerivedMachineFacts Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
}
