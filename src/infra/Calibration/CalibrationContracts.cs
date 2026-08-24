using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.OrcaSlicer;

namespace Farm.Infrastructure.PrinterCalibration;

/// <summary>Stable identifiers shared by calibration capability and context contracts.</summary>
public static class CalibrationContractConstants
{
    public const string ApiVersion = "1.0";
    public const string SchemaVersion = "1.0";
    public const string SlicerEngine = "OrcaSlicer";
    public const string SlicerDistribution = "upstream";
    public const string SlicerVersion = OrcaSlicerVersionConstants.LatestSupported;
    public const string ProfileFormat = "orca-json";
    public const string UpstreamSlicerCapability = "orcaslicer-upstream";

    public static bool AttestsUpstreamSlicer(string? capabilitiesJson)
    {
        if (string.IsNullOrWhiteSpace(capabilitiesJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(capabilitiesJson);
            JsonElement capabilities = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement,
                JsonValueKind.Object when document.RootElement.TryGetProperty(
                    "capabilities",
                    out JsonElement value) => value,
                _ => default,
            };

            return capabilities.ValueKind == JsonValueKind.Array &&
                   capabilities.EnumerateArray().Any(value =>
                       value.ValueKind == JsonValueKind.String &&
                       string.Equals(
                           value.GetString(),
                           UpstreamSlicerCapability,
                           StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed record CalibrationPointDto(
    [property: JsonPropertyName("x"), JsonRequired] double X,
    [property: JsonPropertyName("y"), JsonRequired] double Y);

public sealed record CalibrationExcludedRegionDto(
    [property: JsonPropertyName("name")]
    string? Name,
    [property: JsonPropertyName("polygon"), JsonRequired]
    IReadOnlyList<CalibrationPointDto> Polygon);

public sealed record CalibrationBuildVolumeDto(double? X, double? Y, double? Z);

public sealed record CalibrationBedOriginDto(double? X, double? Y);

public sealed record CalibrationLocationDto(Guid Id, string Name);

public sealed record CalibrationRejectionReasonDto(
    string Code,
    string Field,
    string Message);

public sealed record CalibrationFirmwareIdentityDto(
    string Family,
    string GcodeDialect,
    string DetectionSource,
    string? Version,
    string? DetectionVersion,
    decimal? DetectionConfidence,
    DateTime? DetectedAtUtc,
    bool Verified)
{
    /// <summary>
    /// Builds the recorded firmware identity view directly from a printer's persisted
    /// <c>Firmware*</c> columns. This is the single mapping shared by the calibration
    /// setup endpoint and the printer version endpoint (#1656) so the two views of the
    /// same authoritative store can never diverge from each other.
    /// </summary>
    public static CalibrationFirmwareIdentityDto FromPrinter(Printer printer) =>
        FromPrinter(printer, EffectiveGcodeDialect(printer));

    /// <summary>
    /// Whether <paramref name="printer"/> has a meaningful, semantically-complete recorded
    /// firmware identity — as opposed to a never-probed printer whose <c>Firmware*</c> columns
    /// are still at their unset defaults (<see cref="PrinterFirmwareFamily.Unknown"/>,
    /// <c>null</c> version). This deliberately mirrors the two conditions
    /// <c>CalibrationContextResolver.ValidateFirmware</c> treats as an outright-missing
    /// firmware identity (as opposed to merely "not Klipper", which is a different rejection):
    /// an unset family, or a missing version string. A caller that shows "Recorded" identity in
    /// the UI (#1656, PR #1660 review round 5/Hicks finding) must gate on this — not on mere
    /// non-nullness of a <see cref="CalibrationFirmwareIdentityDto"/> instance — so it can never
    /// present a firmware identity as "recorded" for a printer the calibration gate still
    /// considers to have none at all (e.g. a never-probed printer whose very first live probe
    /// attempt also fails).
    /// </summary>
    public static bool HasRecordedIdentity(Printer printer)
    {
        ArgumentNullException.ThrowIfNull(printer);
        return printer.FirmwareFamily != PrinterFirmwareFamily.Unknown &&
               !string.IsNullOrWhiteSpace(printer.FirmwareVersion);
    }

    /// <summary>
    /// Builds the recorded firmware identity view only when <see cref="HasRecordedIdentity"/> is
    /// true for <paramref name="printer"/>; otherwise returns <c>null</c>. Use this (rather than
    /// the unconditional <see cref="FromPrinter(Printer)"/> overload) for any read path that is
    /// allowed to report "no recorded identity yet" — currently
    /// <see cref="Farm.Infrastructure.Services.Printers.PrinterVersionCache"/> — so a
    /// semantically-empty identity object is never mistaken for a genuinely recorded one.
    /// </summary>
    public static CalibrationFirmwareIdentityDto? FromPrinterIfRecorded(Printer printer) =>
        HasRecordedIdentity(printer) ? FromPrinter(printer) : null;

    /// <summary>
    /// Overload for callers (the calibration eligibility gate) that have already computed the
    /// effective g-code dialect as part of their own validation flow — passing it in avoids
    /// computing it twice while guaranteeing both callers use the exact same fallback rule
    /// (below), so the displayed dialect can never disagree with what the calibration gate
    /// validated.
    /// </summary>
    public static CalibrationFirmwareIdentityDto FromPrinter(Printer printer, PrinterGcodeDialect effectiveGcodeDialect)
    {
        ArgumentNullException.ThrowIfNull(printer);

        string detectionSource = printer.FirmwareDetectionSource switch
        {
            FirmwareDetectionSource.Printer => "printer",
            FirmwareDetectionSource.Configured => "configured",
            _ => "unknown",
        };

        return new CalibrationFirmwareIdentityDto(
            printer.FirmwareFamily.ToString(),
            effectiveGcodeDialect.ToString(),
            detectionSource,
            printer.FirmwareVersion,
            printer.FirmwareDetectionVersion,
            printer.FirmwareDetectionConfidence,
            printer.FirmwareDetectedAtUtc,
            printer.FirmwareIdentityVerified);
    }

    /// <summary>
    /// <c>firmware.gcodeDialect</c> is sourced from firmware detection only, never from the
    /// resolved machine profile (#1613 §4.5/§4.5.1): when the explicit dialect column is unset,
    /// fall back to the dialect implied by the detected firmware family. Mirrors the fallback
    /// the calibration gate's firmware validation applies, so a printer whose firmware family is
    /// known but whose dialect column was never separately populated (e.g. via manual-add
    /// onboarding hints) shows the same effective dialect everywhere.
    /// </summary>
    private static PrinterGcodeDialect EffectiveGcodeDialect(Printer printer) =>
        printer.GcodeDialect != PrinterGcodeDialect.Unknown
            ? printer.GcodeDialect
            : printer.FirmwareFamily == PrinterFirmwareFamily.Klipper
                ? PrinterGcodeDialect.Klipper
                : PrinterGcodeDialect.Unknown;
}

/// <summary>
/// Slicer identity for calibration, including which stored slicer profiles are bound to the
/// printer. The three profile ids are exposed on the read side so an operator surface can show
/// and preselect the current binding; without them a client could write a binding through
/// <c>PUT /api/printers/{id}</c> but never read back what is bound.
/// </summary>
public sealed record CalibrationSlicerIdentityDto(
    string? Engine,
    string? Distribution,
    string? Version,
    string? ProfileFormat,
    Guid? MachineProfileId = null,
    Guid? ProcessProfileId = null,
    Guid? FilamentProfileId = null);

public sealed record CalibrationToolheadDto(
    Guid Id,
    int Index,
    string? Name,
    bool IsPrimary,
    CalibrationPoint3DDto Offset,
    double? NozzleDiameter,
    string? NozzleType,
    string? NozzleMaterial,
    int? NozzleMaxTemperature,
    bool? NozzleIsHardened,
    int? HotendMaxTemperature,
    double? MaxVolumetricFlow,
    string? DriveType,
    bool? IsDirectDrive,
    string? ExtruderGearRatio,
    IReadOnlyList<string>? SupportedMaterials);

public sealed record CalibrationPoint3DDto(double? X, double? Y, double? Z);

public class CalibrationCandidateDto
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public bool InMaintenance { get; init; }

    public PrinterBackend Backend { get; init; }

    public CalibrationLocationDto? Location { get; init; }

    public long ConfigurationRevision { get; init; }

    public string Reachability { get; init; } = "unknown";

    public string OperationalState { get; init; } = "unknown";

    public string StatusSource { get; init; } = "unknown";

    public DateTime? ObservedAtUtc { get; init; }

    public DateTime? LastSeenAtUtc { get; init; }

    public bool IsStale { get; init; }

    public int StaleAfterSeconds { get; init; }

    public bool StatusSupported { get; init; }

    public bool SupportsStatus { get; init; }

    public bool SupportsFileUpload { get; init; }

    public bool SupportsStartPrint { get; init; }

    public bool SupportsUploadAndPrint { get; init; }

    public bool SupportsDirectCommand { get; init; }

    public bool SupportsMultiExtruderStatus { get; init; }

    public CalibrationBuildVolumeDto BuildVolume { get; init; } = new(null, null, null);

    public CalibrationBedOriginDto BedOrigin { get; init; } = new(null, null);

    public IReadOnlyList<CalibrationPointDto>? PrintablePolygon { get; init; }

    public IReadOnlyList<CalibrationExcludedRegionDto>? ExcludedRegions { get; init; }

    public string? MotionType { get; init; }

    public int? MaxPrintSpeed { get; init; }

    public int? MaxTravelSpeed { get; init; }

    public int? MaxAcceleration { get; init; }

    public int? MaxTravelAcceleration { get; init; }

    public int PhysicalToolheadCount { get; init; }

    public int? ActiveToolheadIndex { get; init; }

    public IReadOnlyList<CalibrationToolheadDto> Toolheads { get; init; } = [];

    public bool? HasHeatedBed { get; init; }

    public int? MaxBedTemperature { get; init; }

    public bool? HasEnclosure { get; init; }

    public bool? HasHeatedChamber { get; init; }

    public int? MaxChamberTemperature { get; init; }

    public CalibrationFirmwareIdentityDto Firmware { get; init; } =
        new("Unknown", "Unknown", "unknown", null, null, null, null, false);

    public CalibrationSlicerIdentityDto Slicer { get; init; } =
        new(null, null, null, null);

    public bool ProfilesEvaluated { get; init; }

    public bool Eligible { get; init; }

    public IReadOnlyList<string> MissingInputs { get; init; } = [];

    public IReadOnlyList<CalibrationRejectionReasonDto> RejectionReasons { get; init; } = [];

    /// <summary>
    /// Residual manual-setup fields (issue #1616) that are never sourced from a slicer
    /// profile. Exposed on the base candidate DTO — not just <see cref="CalibrationContextDto"/>
    /// — so fleet-wide callers (e.g. the printers list's calibration-setup onboarding
    /// prompt, issue #1923) can distinguish "never started" from "partially set up"
    /// without a per-printer round trip to the full calibration-context endpoint.
    /// </summary>
    public bool? SupportsPressureAdvance { get; init; }

    public bool? SupportsFirmwareRetraction { get; init; }

    public DateTime? CalibrationHardwareVerifiedAtUtc { get; init; }
}

public sealed class CalibrationContextDto : CalibrationCandidateDto
{
    public CalibrationContextDto(CalibrationCandidateDto candidate)
    {
        Id = candidate.Id;
        Name = candidate.Name;
        Enabled = candidate.Enabled;
        InMaintenance = candidate.InMaintenance;
        Backend = candidate.Backend;
        Location = candidate.Location;
        ConfigurationRevision = candidate.ConfigurationRevision;
        Reachability = candidate.Reachability;
        OperationalState = candidate.OperationalState;
        StatusSource = candidate.StatusSource;
        ObservedAtUtc = candidate.ObservedAtUtc;
        LastSeenAtUtc = candidate.LastSeenAtUtc;
        IsStale = candidate.IsStale;
        StaleAfterSeconds = candidate.StaleAfterSeconds;
        StatusSupported = candidate.StatusSupported;
        SupportsStatus = candidate.SupportsStatus;
        SupportsFileUpload = candidate.SupportsFileUpload;
        SupportsStartPrint = candidate.SupportsStartPrint;
        SupportsUploadAndPrint = candidate.SupportsUploadAndPrint;
        SupportsDirectCommand = candidate.SupportsDirectCommand;
        SupportsMultiExtruderStatus = candidate.SupportsMultiExtruderStatus;
        BuildVolume = candidate.BuildVolume;
        BedOrigin = candidate.BedOrigin;
        PrintablePolygon = candidate.PrintablePolygon;
        ExcludedRegions = candidate.ExcludedRegions;
        MotionType = candidate.MotionType;
        MaxPrintSpeed = candidate.MaxPrintSpeed;
        MaxTravelSpeed = candidate.MaxTravelSpeed;
        MaxAcceleration = candidate.MaxAcceleration;
        MaxTravelAcceleration = candidate.MaxTravelAcceleration;
        PhysicalToolheadCount = candidate.PhysicalToolheadCount;
        ActiveToolheadIndex = candidate.ActiveToolheadIndex;
        Toolheads = candidate.Toolheads;
        HasHeatedBed = candidate.HasHeatedBed;
        MaxBedTemperature = candidate.MaxBedTemperature;
        HasEnclosure = candidate.HasEnclosure;
        HasHeatedChamber = candidate.HasHeatedChamber;
        MaxChamberTemperature = candidate.MaxChamberTemperature;
        Firmware = candidate.Firmware;
        Slicer = candidate.Slicer;
        ProfilesEvaluated = true;
        Eligible = candidate.Eligible;
        MissingInputs = candidate.MissingInputs;
        RejectionReasons = candidate.RejectionReasons;
        SupportsPressureAdvance = candidate.SupportsPressureAdvance;
        SupportsFirmwareRetraction = candidate.SupportsFirmwareRetraction;
        CalibrationHardwareVerifiedAtUtc = candidate.CalibrationHardwareVerifiedAtUtc;
    }

    public string SchemaVersion { get; init; } = CalibrationContractConstants.SchemaVersion;

    public string SnapshotSha256 { get; init; } = string.Empty;

    public DateTime CapturedAtUtc { get; init; }

    public string CapturedBySubject { get; init; } = string.Empty;

    public PrinterConfigurationSnapshotDto Snapshot { get; init; } = new();
}

public sealed record CalibrationProfileDto(
    Guid Id,
    string Kind,
    string Name,
    string SlicerType,
    string? SlicerDistribution,
    string? SlicerVersion,
    string? ProfileFormat,
    string ProfileRevision,
    DateTime? UpdatedAtUtc,
    string? ExactJson,
    string? Sha256);

public sealed record CalibrationProfileSetDto(
    CalibrationProfileDto? Machine,
    CalibrationProfileDto? Process,
    CalibrationProfileDto? Filament);

public sealed record CalibrationBaselineSettingsDto(
    double? ActiveNozzleDiameter,
    double? LayerHeight,
    int? InfillPercentage,
    double? PrintSpeed,
    int? NozzleTemperature,
    int? BedTemperature,
    double? MaxVolumetricFlow);

public sealed record CalibrationRawEffectiveSettingsDto(
    JsonElement? Machine,
    JsonElement? Process,
    JsonElement? Filament);

public sealed record CalibrationFilamentProductChoiceDto(
    Guid ProfileId,
    string Name,
    string Material,
    string? Manufacturer,
    string? Sku);

public sealed record CalibrationSpoolChoiceDto(
    Guid Id,
    string Material,
    string ColorHex,
    double WeightGrams,
    bool InUse,
    Guid? AssignedPrinterId);

public sealed record CalibrationGeneratorCompatibilityDto(
    string ApiVersion,
    string SchemaVersion,
    string SlicerEngine,
    string SlicerDistribution,
    string SlicerVersion,
    IReadOnlyList<string> SupportedCalibrationMethods);

public sealed class PrinterConfigurationSnapshotDto
{
    public string SchemaVersion { get; init; } = CalibrationContractConstants.SchemaVersion;

    public Guid PrinterId { get; init; }

    public long ConfigurationRevision { get; init; }

    public DateTime CapturedAtUtc { get; init; }

    public string CapturedBySubject { get; init; } = string.Empty;

    public CalibrationBuildVolumeDto BuildVolume { get; init; } = new(null, null, null);

    public CalibrationBedOriginDto BedOrigin { get; init; } = new(null, null);

    public IReadOnlyList<CalibrationPointDto>? PrintablePolygon { get; init; }

    public IReadOnlyList<CalibrationExcludedRegionDto>? ExcludedRegions { get; init; }

    public string? MotionType { get; init; }

    public IReadOnlyList<CalibrationToolheadDto> Toolheads { get; init; } = [];

    public bool? HasHeatedBed { get; init; }

    public int? MaxBedTemperature { get; init; }

    public bool? HasEnclosure { get; init; }

    public bool? HasHeatedChamber { get; init; }

    public int? MaxChamberTemperature { get; init; }

    public int? MaxPrintSpeed { get; init; }

    public int? MaxTravelSpeed { get; init; }

    public int? MaxAcceleration { get; init; }

    public int? MaxTravelAcceleration { get; init; }

    public CalibrationFirmwareIdentityDto Firmware { get; init; } =
        new("Unknown", "Unknown", "unknown", null, null, null, null, false);

    public string? BackendVersion { get; init; }

    public string? BackendApiVersion { get; init; }

    public CalibrationSlicerIdentityDto Slicer { get; init; } =
        new(null, null, null, null);

    public CalibrationProfileSetDto Profiles { get; init; } = new(null, null, null);

    public CalibrationBaselineSettingsDto BaselineSettings { get; init; } =
        new(null, null, null, null, null, null, null);

    public CalibrationRawEffectiveSettingsDto RawEffectiveSettings { get; init; } =
        new(null, null, null);

    public IReadOnlyList<CalibrationFilamentProductChoiceDto> FilamentProducts { get; init; } = [];

    public IReadOnlyList<CalibrationSpoolChoiceDto> PhysicalSpools { get; init; } = [];

    public CalibrationGeneratorCompatibilityDto GeneratorCompatibility { get; init; } =
        new(
            CalibrationContractConstants.ApiVersion,
            CalibrationContractConstants.SchemaVersion,
            CalibrationContractConstants.SlicerEngine,
            CalibrationContractConstants.SlicerDistribution,
            CalibrationContractConstants.SlicerVersion,
            []);

    public string SnapshotSha256 { get; init; } = string.Empty;
}

/// <summary>Credential-free profile data exposed by a caller-reachable profile store.</summary>
public sealed record ResolvedCalibrationProfile(
    Guid Id,
    string Kind,
    string Name,
    string SlicerType,
    string? SlicerDistribution,
    string? SlicerVersion,
    string? ProfileFormat,
    DateTime? UpdatedAtUtc,
    string? RawJson,
    string? StoredSha256,
    Guid? PrinterModelId,
    Guid? SpecificPrinterId,
    string? CompatiblePrinters,
    double? LayerHeight,
    int? InfillPercentage,
    double? PrintSpeed,
    int? NozzleTemperature,
    int? BedTemperature,
    double? MaxVolumetricFlow,
    string? Material,
    string? Manufacturer,
    string? Sku,
    IReadOnlyList<CalibrationPointDto>? PrintablePolygon = null,
    double? BedOriginX = null,
    double? BedOriginY = null,
    double? BuildVolumeX = null,
    double? BuildVolumeY = null,
    double? BuildVolumeZ = null,
    CalibrationMotionType? MotionType = null,
    int? MaxAcceleration = null,
    int? MaxTravelSpeed = null,
    bool? HasHeatedBed = null,
    bool? HasHeatedChamber = null,
    double? NozzleDiameter = null,
    NozzleType? NozzleType = null,
    int? NozzleMaxTemperature = null,
    int? HotendMaxTemperature = null)
{
    /// <summary>
    /// Machine-profile facts derivable from <see cref="RawJson"/> (#1613 §4.3, #1615 PR-2).
    /// Populated once by the producer (<c>CalibrationProfileResolver.MapMachine</c>) via the
    /// shared <c>Farm.Slicer.ProfileParsing</c> library, so both the monolith and split
    /// deployments expose the same typed facts over this DTO without <c>src/api</c> ever parsing
    /// OrcaSlicer JSON itself. Only ever populated on the <c>"machine"</c>-kind profile.
    /// </summary>
}

public sealed record ResolvedCalibrationProfiles(
    ResolvedCalibrationProfile? Machine,
    ResolvedCalibrationProfile? Process,
    ResolvedCalibrationProfile? Filament);

/// <summary>Caller scope used to hide private profiles from non-owners.</summary>
public sealed record CalibrationProfileAccessScope(
    Guid? UserId,
    bool BypassOwnership);

public sealed class CalibrationProfileResolverUnavailableException : Exception
{
    public CalibrationProfileResolverUnavailableException()
        : base("The calibration profile resolver is unavailable.")
    {
        ErrorCode = "profile_service_unavailable";
    }

    public CalibrationProfileResolverUnavailableException(string message)
        : base(message)
    {
        ErrorCode = "profile_service_unavailable";
    }

    public CalibrationProfileResolverUnavailableException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = "profile_service_unavailable";
    }

    public CalibrationProfileResolverUnavailableException(
        string message,
        string errorCode,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>Resolves explicitly selected profiles without exposing an internal service address.</summary>
public interface ICalibrationProfileResolver
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

    Task<ResolvedCalibrationProfiles> ResolveAsync(
        Guid machineProfileId,
        Guid processProfileId,
        Guid filamentProfileId,
        CalibrationProfileAccessScope accessScope,
        CancellationToken cancellationToken);
}
