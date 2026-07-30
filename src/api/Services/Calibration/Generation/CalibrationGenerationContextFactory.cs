using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// The pinned upstream slicer identity a registered worker actually attested.
/// </summary>
/// <param name="Version">Reported slicer version.</param>
/// <param name="Distribution">Reported slicer distribution.</param>
/// <param name="ContainerDigest">Reported container digest of the pinned image.</param>
/// <param name="BinarySha256">Reported digest of the pinned slicer binary.</param>
/// <param name="WorkerId">Worker that published the attestation.</param>
public sealed record CalibrationPinnedSlicerIdentity(
    string Version,
    string Distribution,
    string ContainerDigest,
    string BinarySha256,
    Guid WorkerId);

/// <summary>
/// Rebuilds the authoritative generation context of an immutable attempt from its stored snapshot.
/// </summary>
/// <remarks>
/// Nothing here is taken from the caller. Geometry, limits, firmware identity, profile documents and
/// digests come from the immutable <see cref="PrinterConfigurationSnapshot"/> captured when the attempt
/// was created, the pinned slicer identity comes from a worker attestation, and the current printer
/// revision comes from the authoritative printer record. A missing authoritative value is reported as a
/// rejection reason rather than replaced with a default.
/// </remarks>
public static class CalibrationGenerationContextFactory
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Builds the authoritative context for one attempt.
    /// </summary>
    /// <param name="project">The owning calibration project.</param>
    /// <param name="attempt">The immutable attempt.</param>
    /// <param name="orchestration">The durable orchestration adopted for the run.</param>
    /// <param name="snapshot">The immutable printer configuration snapshot of the attempt.</param>
    /// <param name="currentPrinterConfigurationRevision">The printer revision observed now.</param>
    /// <param name="pinned">The pinned slicer identity attested by an eligible worker.</param>
    /// <param name="importedAsset">The linked stored model, when the method requires one.</param>
    /// <returns>The rebuilt context, or the ordered rejection reasons.</returns>
    public static CalibrationGenerationResult<CalibrationGenerationContext> Build(
        CalibrationProject project,
        CalibrationAttempt attempt,
        CalibrationOrchestration orchestration,
        PrinterConfigurationSnapshot snapshot,
        long currentPrinterConfigurationRevision,
        CalibrationPinnedSlicerIdentity pinned,
        CalibrationModelReference? importedAsset)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(orchestration);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(pinned);

        PrinterConfigurationSnapshotDto? document;
        try
        {
            document = JsonSerializer.Deserialize<PrinterConfigurationSnapshotDto>(
                snapshot.SanitizedSnapshotJson,
                SnapshotJsonOptions);
        }
        catch (JsonException)
        {
            document = null;
        }

        if (document is null)
        {
            return CalibrationGenerationResults.Failure<CalibrationGenerationContext>(
                CalibrationGenerationProblemCodes.ContextIdentityMissing,
                "attempt.printerConfigurationSnapshot",
                "The immutable printer configuration snapshot could not be read.");
        }

        CalibrationToolheadDto? toolhead = SelectToolhead(project, document);
        if (toolhead is null)
        {
            return CalibrationGenerationResults.Failure<CalibrationGenerationContext>(
                CalibrationGenerationProblemCodes.ToolheadMissing,
                "context.toolhead",
                "The immutable snapshot does not describe the selected toolhead.");
        }

        DateTime capturedAtUtc = DateTime.SpecifyKind(snapshot.CapturedAtUtc, DateTimeKind.Utc);
        return CalibrationGenerationResults.Success(new CalibrationGenerationContext
        {
            ProjectId = project.Id,
            AttemptId = attempt.Id,
            OrchestrationId = orchestration.Id,
            PrinterId = snapshot.PrinterId,
            PrinterConfigurationSnapshotId = snapshot.Id,
            PrinterConfigurationRevision = snapshot.PrinterConfigurationRevision,
            PrinterConfigurationSnapshotSha256 = snapshot.SnapshotSha256,
            CurrentPrinterConfigurationRevision = currentPrinterConfigurationRevision,
            SnapshotCapturedAtUtc = capturedAtUtc,
            Compatibility = new CalibrationCompatibilityIdentity(
                snapshot.FirmwareFamily.ToString(),
                snapshot.GcodeDialect.ToString(),
                Blank(snapshot.SlicerEngine),
                Blank(snapshot.SlicerDistribution),
                snapshot.SlicerVersion,
                snapshot.SlicerContainerDigest ?? pinned.ContainerDigest,
                pinned.BinarySha256,
                document.Profiles.Machine?.ProfileFormat ?? CalibrationContractConstants.ProfileFormat),
            Firmware = new CalibrationFirmwareContext(
                snapshot.FirmwareFamily.ToString(),
                snapshot.FirmwareVersion,
                document.Firmware.DetectionSource,
                snapshot.GcodeDialect.ToString(),
                document.Firmware.Verified,
                document.Firmware.DetectedAtUtc ?? capturedAtUtc),
            Toolhead = new CalibrationToolheadContext(
                toolhead.Id,
                toolhead.Index,
                Decimal(toolhead.NozzleDiameter) ?? 0m,
                toolhead.NozzleType,
                toolhead.NozzleMaterial,
                toolhead.NozzleMaxTemperature,
                toolhead.HotendMaxTemperature,
                Decimal(toolhead.MaxVolumetricFlow),
                toolhead.IsDirectDrive),
            Bed = new CalibrationBedGeometry(
                Decimal(document.BuildVolume.X),
                Decimal(document.BuildVolume.Y),
                Decimal(document.BuildVolume.Z),
                Decimal(document.BedOrigin.X),
                Decimal(document.BedOrigin.Y),
                MapPolygon(document.PrintablePolygon),
                MapExcludedRegions(document.ExcludedRegions)),
            Limits = new CalibrationMachineLimits(
                document.MaxBedTemperature,
                document.HasHeatedChamber,
                document.MaxChamberTemperature,
                document.MaxPrintSpeed,
                document.MaxTravelSpeed,
                document.MaxAcceleration,
                document.MaxTravelAcceleration),
            Filament = BuildFilament(project, document, snapshot),
            Process = BuildProcess(document),
            Profiles = new CalibrationProfileTriplet(
                Profile(snapshot.MachineProfileId, "machine", document.Profiles.Machine, snapshot.ExactMachineProfileJson, snapshot.MachineProfileSha256),
                Profile(snapshot.ProcessProfileId, "process", document.Profiles.Process, snapshot.ExactProcessProfileJson, snapshot.ProcessProfileSha256),
                Profile(snapshot.FilamentProfileId, "filament", document.Profiles.Filament, snapshot.ExactFilamentProfileJson, snapshot.FilamentProfileSha256)),
            Generator = CalibrationGeneratorIdentity.Current,
            OperationId = orchestration.OperationId,
            ImportedAsset = importedAsset,
        });
    }

    private static CalibrationToolheadDto? SelectToolhead(
        CalibrationProject project,
        PrinterConfigurationSnapshotDto document)
    {
        if (document.Toolheads.Count == 0)
        {
            return null;
        }

        if (project.SelectedToolheadId is { } selectedId &&
            document.Toolheads.FirstOrDefault(candidate => candidate.Id == selectedId) is { } byId)
        {
            return byId;
        }

        if (project.SelectedToolheadIndex is { } selectedIndex &&
            document.Toolheads.FirstOrDefault(candidate => candidate.Index == selectedIndex) is { } byIndex)
        {
            return byIndex;
        }

        return document.Toolheads.FirstOrDefault(candidate => candidate.IsPrimary) ?? document.Toolheads[0];
    }

    private static CalibrationFilamentContext BuildFilament(
        CalibrationProject project,
        PrinterConfigurationSnapshotDto document,
        PrinterConfigurationSnapshot snapshot)
    {
        CalibrationFilamentProductChoiceDto? product = document.FilamentProducts
            .FirstOrDefault(candidate => candidate.ProfileId == snapshot.FilamentProfileId);
        return new CalibrationFilamentContext(
            snapshot.FilamentProfileId ?? Guid.Empty,
            product?.Material ?? Blank(project.FilamentMaterial),
            product?.Sku ?? project.FilamentSku,
            product?.Manufacturer ?? project.FilamentVendor,
            project.FilamentDiameter,
            document.BaselineSettings.NozzleTemperature,
            document.BaselineSettings.BedTemperature,
            null,
            null,
            Decimal(document.BaselineSettings.MaxVolumetricFlow),
            project.LocalSpoolId,
            null);
    }

    private static CalibrationProcessContext BuildProcess(PrinterConfigurationSnapshotDto document) =>
        new(
            Decimal(document.BaselineSettings.LayerHeight),
            null,
            null,
            Whole(document.BaselineSettings.PrintSpeed),
            null,
            document.MaxTravelSpeed,
            null,
            null,
            null,
            null);

    private static CalibrationExactProfile? Profile(
        Guid? id,
        string kind,
        CalibrationProfileDto? described,
        string? exactJson,
        string? sha256) =>
        id is { } value
            ? new CalibrationExactProfile(value, kind, described?.Name, described?.ProfileRevision, exactJson, sha256)
            : null;

    private static IReadOnlyList<CalibrationBedPoint> MapPolygon(
        IReadOnlyList<CalibrationPointDto>? polygon) =>
        polygon is null
            ? []
            : [.. polygon.Select(point => new CalibrationBedPoint((decimal)point.X, (decimal)point.Y))];

    private static IReadOnlyList<CalibrationExcludedRegion> MapExcludedRegions(
        IReadOnlyList<CalibrationExcludedRegionDto>? regions) =>
        regions is null
            ? []
            : [.. regions.Select(region => new CalibrationExcludedRegion(
                region.Name ?? string.Empty,
                MapPolygon(region.Polygon)))];

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static decimal? Decimal(double? value) =>
        value is { } number && !double.IsNaN(number) && !double.IsInfinity(number)
            ? decimal.Round((decimal)number, 4)
            : null;

    private static int? Whole(double? value) =>
        value is { } number && !double.IsNaN(number) && !double.IsInfinity(number)
            ? (int)Math.Round(number, MidpointRounding.AwayFromZero)
            : null;
}
