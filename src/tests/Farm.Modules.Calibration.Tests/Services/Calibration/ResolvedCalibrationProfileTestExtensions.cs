using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Services;

namespace Farm.Web.Api.Tests.Services.Calibration;

/// <summary>
/// Test-only helper that keeps hand-built <see cref="ResolvedCalibrationProfile"/> fixtures'
/// typed derived fields in sync with their <see cref="ResolvedCalibrationProfile.RawJson"/>
/// (#1615 PR-2). Production now populates those typed fields once, producer-side, via
/// <see cref="MachineProfileDerivedFieldsExtractor"/> (see
/// <c>CalibrationProfileResolver.MapMachine</c>) instead of leaving <c>src/api</c> to parse
/// <c>RawJson</c> itself. Mocked-resolver test fixtures that only set <c>RawJson</c> directly
/// would silently lose every profile-derived fact once the deriver stopped parsing it, so this
/// extension recomputes them the same way production does &#8212; single-sourcing the fixtures
/// from the real extraction logic rather than requiring tests to hand-compute expected values.
/// </summary>
public static class ResolvedCalibrationProfileTestExtensions
{
    public static ResolvedCalibrationProfile WithRawJson(
        this ResolvedCalibrationProfile profile,
        string? rawJson)
    {
        MachineProfileDerivedFields derived = MachineProfileDerivedFieldsExtractor.Extract(rawJson);
        return profile with
        {
            RawJson = rawJson,
            PrintablePolygon = derived.PrintablePolygon,
            BedOriginX = derived.BedOriginX,
            BedOriginY = derived.BedOriginY,
            BuildVolumeX = derived.BuildVolumeX,
            BuildVolumeY = derived.BuildVolumeY,
            BuildVolumeZ = derived.BuildVolumeZ,
            MotionType = derived.MotionType,
            MaxAcceleration = derived.MaxAcceleration,
            MaxTravelSpeed = derived.MaxTravelSpeed,
            HasHeatedBed = derived.HasHeatedBed,
            HasHeatedChamber = derived.HasHeatedChamber,
            NozzleDiameter = derived.NozzleDiameter,
            NozzleType = derived.NozzleType,
            NozzleMaxTemperature = derived.NozzleMaxTemperature,
            HotendMaxTemperature = derived.HotendMaxTemperature,
        };
    }
}
