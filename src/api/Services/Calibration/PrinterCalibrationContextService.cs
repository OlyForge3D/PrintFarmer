using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Calibration;

public interface IPrinterCalibrationContextService
{
    Task<CalibrationServiceResult<IReadOnlyList<CalibrationCandidateDto>>> GetCandidatesAsync(
        CalibrationProfileAccessScope profileAccessScope,
        CancellationToken cancellationToken);

    Task<CalibrationServiceResult<CalibrationContextDto>> GetContextAsync(
        Guid printerId,
        long? configurationRevision,
        string capturedBySubject,
        CalibrationProfileAccessScope profileAccessScope,
        CancellationToken cancellationToken);
}

public sealed record CalibrationServiceResult<T>(
    T? Value,
    string? ErrorCode = null,
    long? CurrentConfigurationRevision = null)
    where T : class;

public sealed class PrinterCalibrationContextService(
    AppDbContext dbContext,
    IPrinterStatusSnapshotReader statusReader,
    IBackendCapabilityFactory capabilityFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ICalibrationProfileResolver? profileResolver = null,
    CalibrationSlicerCompatibilityPolicy? compatibilityPolicy = null)
    : IPrinterCalibrationContextService
{
    private const double NumericTolerance = 0.001;
    private readonly int _statusStaleAfterSeconds =
        Math.Max(1, configuration.GetValue("Calibration:StatusStaleAfterSeconds", 30));

    private readonly int _firmwareStaleAfterSeconds =
        Math.Max(1, configuration.GetValue("Calibration:FirmwareMetadataStaleAfterSeconds", 86_400));

    private readonly int _hardwareStaleAfterSeconds =
        Math.Max(1, configuration.GetValue("Calibration:HardwareMetadataStaleAfterSeconds", 2_592_000));

    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly CalibrationSlicerCompatibilityPolicy _compatibilityPolicy =
        compatibilityPolicy ?? CalibrationSlicerCompatibilityPolicy.Default;

    public async Task<CalibrationServiceResult<IReadOnlyList<CalibrationCandidateDto>>> GetCandidatesAsync(
        CalibrationProfileAccessScope profileAccessScope,
        CancellationToken cancellationToken)
    {
        Guid? unknownModelId = await GetUnknownModelIdAsync(cancellationToken);
        List<Printer> printers = await dbContext.Printers
            .AsNoTracking()
            .Where(printer => printer.IsEnabled)
            .Include(printer => printer.Location)
            .Include(printer => printer.Toolheads)
            .Include(printer => printer.Model).ThenInclude(model => model!.Toolheads).ThenInclude(t => t.HotendModel)
            .Include(printer => printer.Model).ThenInclude(model => model!.Toolheads).ThenInclude(t => t.ExtruderModel)
            .Include(printer => printer.Model).ThenInclude(model => model!.Toolheads).ThenInclude(t => t.NozzleModel!)
                .ThenInclude(n => n!.NozzleMaterial)
            .AsSplitQuery()
            .OrderBy(printer => printer.Name)
            .ThenBy(printer => printer.Id)
            .ToListAsync(cancellationToken);

        List<CalibrationCandidateDto> candidates = new(printers.Count);
        foreach (Printer printer in printers)
        {
            CalibrationEvaluation evaluation = await EvaluateAsync(
                printer,
                profileAccessScope,
                resolveProfiles: false,
                unknownModelId,
                cancellationToken);
            candidates.Add(evaluation.Candidate);
        }

        return new(candidates);
    }

    public async Task<CalibrationServiceResult<CalibrationContextDto>> GetContextAsync(
        Guid printerId,
        long? configurationRevision,
        string capturedBySubject,
        CalibrationProfileAccessScope profileAccessScope,
        CancellationToken cancellationToken)
    {
        Printer? printer = await dbContext.Printers
            .AsNoTracking()
            .Where(candidate => candidate.Id == printerId && candidate.IsEnabled)
            .Include(candidate => candidate.Location)
            .Include(candidate => candidate.Toolheads)
            .Include(candidate => candidate.Model).ThenInclude(model => model!.Toolheads).ThenInclude(t => t.HotendModel)
            .Include(candidate => candidate.Model).ThenInclude(model => model!.Toolheads).ThenInclude(t => t.ExtruderModel)
            .Include(candidate => candidate.Model).ThenInclude(model => model!.Toolheads).ThenInclude(t => t.NozzleModel!)
                .ThenInclude(n => n!.NozzleMaterial)
            .AsSplitQuery()
            .SingleOrDefaultAsync(cancellationToken);
        if (printer is null)
        {
            return new(null, "printer_not_found");
        }

        if (configurationRevision.HasValue &&
            configurationRevision.Value != printer.ConfigurationRevision)
        {
            return new(
                null,
                "printer_configuration_changed",
                printer.ConfigurationRevision);
        }

        CalibrationEvaluation evaluation;
        try
        {
            Guid? unknownModelId = await GetUnknownModelIdAsync(cancellationToken);
            evaluation = await EvaluateAsync(
                printer,
                profileAccessScope,
                resolveProfiles: true,
                unknownModelId,
                cancellationToken);
        }
        catch (CalibrationProfileResolverUnavailableException exception)
        {
            return new(null, exception.ErrorCode);
        }

        DateTime capturedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        List<Spool> spools = await dbContext.Spools
            .AsNoTracking()
            .Where(spool =>
                spool.AssignedPrinterId == null ||
                spool.AssignedPrinterId == printer.Id)
            .OrderBy(spool => spool.Material)
            .ThenBy(spool => spool.Id)
            .ToListAsync(cancellationToken);

        IReadOnlyList<CalibrationSpoolChoiceDto> spoolChoices = spools
            .Select(spool => new CalibrationSpoolChoiceDto(
                spool.Id,
                spool.Material,
                spool.ColorHex,
                spool.WeightGrams,
                spool.InUse,
                spool.AssignedPrinterId))
            .ToArray();

        IReadOnlyList<string> methods = GetSupportedCalibrationMethods(printer);
        CalibrationGeneratorCompatibilityDto generatorCompatibility = new(
            CalibrationContractConstants.ApiVersion,
            CalibrationContractConstants.SchemaVersion,
            CalibrationContractConstants.SlicerEngine,
            CalibrationContractConstants.SlicerDistribution,
            _compatibilityPolicy.RequiredVersion,
            methods);

        object hashInput = new
        {
            schemaVersion = CalibrationContractConstants.SchemaVersion,
            printerId = printer.Id,
            configurationRevision = printer.ConfigurationRevision,
            buildVolume = evaluation.Candidate.BuildVolume,
            bedOrigin = evaluation.Candidate.BedOrigin,
            printablePolygon = evaluation.Candidate.PrintablePolygon,
            excludedRegions = evaluation.Candidate.ExcludedRegions,
            motionType = evaluation.Candidate.MotionType,
            maxPrintSpeed = evaluation.Candidate.MaxPrintSpeed,
            maxTravelSpeed = evaluation.Candidate.MaxTravelSpeed,
            maxAcceleration = evaluation.Candidate.MaxAcceleration,
            maxTravelAcceleration = evaluation.Candidate.MaxTravelAcceleration,
            toolheads = evaluation.Candidate.Toolheads,
            activeToolheadIndex = evaluation.Candidate.ActiveToolheadIndex,
            hasHeatedBed = evaluation.Candidate.HasHeatedBed,
            maxBedTemperature = evaluation.Candidate.MaxBedTemperature,
            hasEnclosure = evaluation.Candidate.HasEnclosure,
            hasHeatedChamber = evaluation.Candidate.HasHeatedChamber,
            maxChamberTemperature = evaluation.Candidate.MaxChamberTemperature,
            firmware = evaluation.Candidate.Firmware,
            backendVersion = printer.BackendVersion,
            backendApiVersion = printer.BackendApiVersion,
            slicer = evaluation.Candidate.Slicer,
            profiles = new
            {
                machine = CreateProfileHashInput(
                    evaluation.Profiles.Machine,
                    evaluation.RawEffectiveSettings.Machine),
                process = CreateProfileHashInput(
                    evaluation.Profiles.Process,
                    evaluation.RawEffectiveSettings.Process),
                filament = CreateProfileHashInput(
                    evaluation.Profiles.Filament,
                    evaluation.RawEffectiveSettings.Filament),
            },
            baselineSettings = evaluation.BaselineSettings,
            supportsPressureAdvance = printer.SupportsPressureAdvance,
            supportsFirmwareRetraction = printer.SupportsFirmwareRetraction,
        };
        string snapshotSha256 = CalibrationSnapshotBuilder.ComputeSha256(hashInput);

        PrinterConfigurationSnapshotDto snapshot = new()
        {
            PrinterId = printer.Id,
            ConfigurationRevision = printer.ConfigurationRevision,
            CapturedAtUtc = capturedAtUtc,
            CapturedBySubject = capturedBySubject,
            BuildVolume = evaluation.Candidate.BuildVolume,
            BedOrigin = evaluation.Candidate.BedOrigin,
            PrintablePolygon = evaluation.Candidate.PrintablePolygon,
            ExcludedRegions = evaluation.Candidate.ExcludedRegions,
            MotionType = evaluation.Candidate.MotionType,
            Toolheads = evaluation.Candidate.Toolheads,
            HasHeatedBed = evaluation.Candidate.HasHeatedBed,
            MaxBedTemperature = evaluation.Candidate.MaxBedTemperature,
            HasEnclosure = evaluation.Candidate.HasEnclosure,
            HasHeatedChamber = evaluation.Candidate.HasHeatedChamber,
            MaxChamberTemperature = evaluation.Candidate.MaxChamberTemperature,
            MaxPrintSpeed = evaluation.Candidate.MaxPrintSpeed,
            MaxTravelSpeed = evaluation.Candidate.MaxTravelSpeed,
            MaxAcceleration = evaluation.Candidate.MaxAcceleration,
            MaxTravelAcceleration = evaluation.Candidate.MaxTravelAcceleration,
            Firmware = evaluation.Candidate.Firmware,
            BackendVersion = printer.BackendVersion,
            BackendApiVersion = printer.BackendApiVersion,
            Slicer = evaluation.Candidate.Slicer,
            Profiles = evaluation.Profiles,
            BaselineSettings = evaluation.BaselineSettings,
            RawEffectiveSettings = evaluation.RawEffectiveSettings,
            FilamentProducts = evaluation.FilamentProducts,
            PhysicalSpools = spoolChoices,
            GeneratorCompatibility = generatorCompatibility,
            SnapshotSha256 = snapshotSha256,
        };

        CalibrationContextDto context = new(evaluation.Candidate)
        {
            SnapshotSha256 = snapshotSha256,
            CapturedAtUtc = capturedAtUtc,
            CapturedBySubject = capturedBySubject,
            SupportsPressureAdvance = printer.SupportsPressureAdvance,
            SupportsFirmwareRetraction = printer.SupportsFirmwareRetraction,
            CalibrationHardwareVerifiedAtUtc = printer.CalibrationHardwareVerifiedAtUtc,
            Snapshot = snapshot,
        };
        return new(context);
    }

    private async Task<Guid?> GetUnknownModelIdAsync(CancellationToken cancellationToken)
    {
        // Resolve the reserved "Unknown" manufacturer + "Unknown Model" catalog row -- the same
        // sentinel every printer's non-nullable ModelId resolves to by default (see
        // EfCatalogRepository.GetUnknownModelIdAsync for the identical, production-side
        // identity). Matching Name alone would be insufficient: PrinterModel only enforces
        // uniqueness on (ManufacturerId, Name), so an admin could otherwise curate a real model
        // named "Unknown Model" under a different manufacturer and have its facts wrongly
        // discarded (#1922).
        Guid? unknownManufacturerId = await dbContext.Manufacturers
            .AsNoTracking()
            .Where(manufacturer => manufacturer.Name == "Unknown")
            .Select(manufacturer => (Guid?)manufacturer.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (unknownManufacturerId is null)
        {
            return null;
        }

        return await dbContext.PrinterModels
            .AsNoTracking()
            .Where(model =>
                model.ManufacturerId == unknownManufacturerId.Value &&
                model.Name == CalibrationCatalogModelDeriver.UnknownModelName)
            .Select(model => (Guid?)model.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<CalibrationEvaluation> EvaluateAsync(
        Printer printer,
        CalibrationProfileAccessScope profileAccessScope,
        bool resolveProfiles,
        Guid? unknownModelId,
        CancellationToken cancellationToken)
    {
        List<CalibrationRejectionReasonDto> reasons = [];
        HashSet<string> missingInputs = new(StringComparer.Ordinal);
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        BackendCapabilities backendCapabilities =
            capabilityFactory.GetSupportedCapabilities((PrinterBackend)printer.Backend);
        bool supportsStatus =
            backendCapabilities.HasFlag(BackendCapabilities.Status);
        bool supportsFileUpload =
            backendCapabilities.HasFlag(BackendCapabilities.FileUpload);
        bool supportsStartPrint =
            backendCapabilities.HasFlag(BackendCapabilities.StartPrint);
        bool supportsUploadAndPrint =
            backendCapabilities.HasFlag(BackendCapabilities.UploadAndPrint);
        bool supportsDirectCommand =
            backendCapabilities.HasFlag(BackendCapabilities.DirectCommand);
        const bool supportsMultiExtruderStatus = false;

        PrinterStatusSnapshot? status = statusReader.GetStatusSnapshot(printer.Id);
        bool isStale = supportsStatus &&
            (status?.ObservedAtUtc is not DateTime observedAtUtc ||
                nowUtc - observedAtUtc >
                TimeSpan.FromSeconds(_statusStaleAfterSeconds));
        string reachability = !supportsStatus
            ? "unsupported"
            : status is null
                ? "unknown"
                : status.Status.IsOnline
                    ? "online"
                    : "offline";
        string operationalState = NormalizeOperationalState(status?.Status);
        string statusSource = !supportsStatus
            ? "unsupported"
            : status?.Source ?? "unknown";

        if (!supportsStatus)
        {
            Reject(
                reasons,
                missingInputs,
                "status_unsupported",
                "statusSupported",
                "The configured adapter does not declare status support.");
        }
        else if (status?.ObservedAtUtc is null)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "status_unknown",
                "observedAtUtc",
                "No authoritative status observation is available.");
        }
        else if (isStale)
        {
            Reject(
                reasons,
                missingInputs,
                "status_stale",
                "observedAtUtc",
                "The latest status observation is stale.");
        }
        else if (!status.Status.IsOnline)
        {
            Reject(
                reasons,
                missingInputs,
                "printer_offline",
                "reachability",
                "The latest fresh observation reports the printer offline.");
        }

        if (!supportsUploadAndPrint && !(supportsFileUpload && supportsStartPrint))
        {
            Reject(
                reasons,
                missingInputs,
                "required_operations_unsupported",
                "executionCapabilities",
                "The configured adapter must support upload and start-print operations.");
        }

        if (printer.InMaintenance)
        {
            Reject(
                reasons,
                missingInputs,
                "printer_in_maintenance",
                "inMaintenance",
                "The printer is in maintenance mode.");
        }

        CalibrationProfileSelection? profileSelection =
            ValidateProfileSelection(printer, reasons, missingInputs);

        List<Toolhead> physicalToolheads = printer.Toolheads
            .Where(toolhead => toolhead.ToolheadType == ToolheadType.Physical)
            .OrderBy(toolhead => toolhead.Index)
            .ThenBy(toolhead => toolhead.Id)
            .ToList();

        // #1922: the catalog model is a pure in-memory projection of an entity already loaded
        // via the query's Include chain, so it requires no additional resolver/database call
        // and can be computed unconditionally ahead of the (expensive) profile resolution.
        DerivedCatalogFacts catalogFacts = CalibrationCatalogModelDeriver.Derive(printer.Model, unknownModelId);
        int? activeToolheadIndex = printer.ActiveToolheadIndex ?? catalogFacts.ActiveToolheadIndex;
        bool isSingleToolhead = physicalToolheads.Count == 1;

        ResolvedCalibrationProfiles? resolved = null;
        if (profileSelection is not null)
        {
            if (resolveProfiles)
            {
                // GetContextAsync's caller preserves the existing contract of propagating
                // CalibrationProfileResolverUnavailableException uncaught.
                resolved = await ResolveProfilesAsync(
                    profileSelection,
                    profileAccessScope,
                    cancellationToken);
            }
            else if (NeedsMachineProfileDerivation(printer, physicalToolheads, activeToolheadIndex))
            {
                // The candidates path must still resolve profiles once when (and only when)
                // AC-2-derivable fields are missing, so eligibility reflects sourced values.
                // A resolver failure here degrades to a typed rejection rather than throwing,
                // preserving GetCandidatesAsync's existing zero-resolver-call test guarantees
                // for fully-populated printers.
                try
                {
                    resolved = await ResolveProfilesAsync(
                        profileSelection,
                        profileAccessScope,
                        cancellationToken);
                }
                catch (CalibrationProfileResolverUnavailableException ex)
                {
                    Reject(
                        reasons,
                        missingInputs,
                        ex.ErrorCode,
                        "machineProfile",
                        "The calibration profile resolver is unavailable.");
                }
            }
        }

        DerivedMachineFacts derivedFacts =
            CalibrationMachineProfileDeriver.Derive(resolved?.Machine);

        PrinterGcodeDialect effectiveGcodeDialect =
            ValidateFirmware(printer, nowUtc, reasons, missingInputs);
        CalibrationSlicerIdentityDto slicerIdentity =
            ValidateSlicerIdentity(printer, resolved?.Machine, reasons, missingInputs);

        IReadOnlyList<CalibrationPointDto>? printablePolygon =
            printer.PrintablePolygonJson is null
                ? derivedFacts.PrintablePolygon ?? catalogFacts.PrintablePolygon
                : ParseJsonList<CalibrationPointDto>(
                    printer.PrintablePolygonJson,
                    "printablePolygon",
                    reasons,
                    missingInputs);
        IReadOnlyList<CalibrationExcludedRegionDto>? excludedRegions =
            printer.ExcludedRegionsJson is null
                ? []
                : ParseJsonList<CalibrationExcludedRegionDto>(
                    printer.ExcludedRegionsJson,
                    "excludedRegions",
                    reasons,
                    missingInputs);
        if (printablePolygon is { Count: < 3 })
        {
            Reject(
                reasons,
                missingInputs,
                "printable_polygon_invalid",
                "printablePolygon",
                "The printable polygon must contain at least three points.");
        }

        CalibrationToolheadDto[] toolheads = physicalToolheads
            .Select(toolhead => MapToolhead(
                toolhead,
                activeToolheadIndex,
                derivedFacts,
                catalogFacts,
                isSingleToolhead))
            .ToArray();

        EffectiveHardwareFacts hardwareFacts = ValidateHardware(
            printer,
            physicalToolheads,
            printablePolygon,
            derivedFacts,
            catalogFacts,
            activeToolheadIndex,
            isSingleToolhead,
            supportsMultiExtruderStatus,
            nowUtc,
            reasons,
            missingInputs);

        CalibrationProfileEvaluation profileEvaluation =
            resolveProfiles && profileSelection is not null && resolved is not null
            ? EvaluateProfiles(
                printer,
                resolved,
                toolheads,
                hardwareFacts,
                activeToolheadIndex,
                reasons,
                missingInputs)
            : CalibrationProfileEvaluation.Empty;

        CalibrationCandidateDto candidate = new()
        {
            Id = printer.Id,
            Name = printer.Name,
            Enabled = printer.IsEnabled,
            InMaintenance = printer.InMaintenance,
            Backend = (PrinterBackend)printer.Backend,
            Location = printer.Location is null
                ? null
                : new CalibrationLocationDto(printer.Location.Id, printer.Location.Name),
            ConfigurationRevision = printer.ConfigurationRevision,
            Reachability = reachability,
            OperationalState = operationalState,
            StatusSource = statusSource,
            ObservedAtUtc = status?.ObservedAtUtc,
            LastSeenAtUtc = status?.LastSeenAtUtc,
            IsStale = isStale,
            StaleAfterSeconds = _statusStaleAfterSeconds,
            StatusSupported = supportsStatus,
            SupportsStatus = supportsStatus,
            SupportsFileUpload = supportsFileUpload,
            SupportsStartPrint = supportsStartPrint,
            SupportsUploadAndPrint = supportsUploadAndPrint,
            SupportsDirectCommand = supportsDirectCommand,
            SupportsMultiExtruderStatus = supportsMultiExtruderStatus,
            BuildVolume = new(
                hardwareFacts.BuildVolumeX,
                hardwareFacts.BuildVolumeY,
                hardwareFacts.BuildVolumeZ),
            BedOrigin = new(hardwareFacts.BedOriginX, hardwareFacts.BedOriginY),
            PrintablePolygon = printablePolygon,
            ExcludedRegions = excludedRegions,
            MotionType = hardwareFacts.MotionType?.ToString(),
            MaxPrintSpeed = printer.MaxPrintSpeed,
            MaxTravelSpeed = hardwareFacts.MaxTravelSpeed,
            MaxAcceleration = hardwareFacts.MaxAcceleration,
            MaxTravelAcceleration = hardwareFacts.MaxTravelAcceleration,
            PhysicalToolheadCount = physicalToolheads.Count,
            ActiveToolheadIndex = activeToolheadIndex,
            Toolheads = toolheads,
            HasHeatedBed = hardwareFacts.HasHeatedBed,
            MaxBedTemperature = printer.MaxBedTemp,
            HasEnclosure = hardwareFacts.HasEnclosure,
            HasHeatedChamber = hardwareFacts.HasHeatedChamber,
            MaxChamberTemperature = printer.MaxChamberTemp,
            Firmware = CalibrationFirmwareIdentityDto.FromPrinter(printer, effectiveGcodeDialect),
            Slicer = slicerIdentity,
            ProfilesEvaluated = resolveProfiles,
            Eligible = reasons.Count == 0,
            MissingInputs = missingInputs.Order(StringComparer.Ordinal).ToArray(),
            RejectionReasons = reasons
                .OrderBy(reason => reason.Code, StringComparer.Ordinal)
                .ThenBy(reason => reason.Field, StringComparer.Ordinal)
                .ToArray(),
        };

        return new(
            candidate,
            profileEvaluation.Profiles,
            profileEvaluation.BaselineSettings,
            profileEvaluation.RawEffectiveSettings,
            profileEvaluation.FilamentProducts);
    }

    /// <summary>
    /// Pure check (no resolver calls) for whether any AC-2-derivable field is missing its
    /// explicit value after accounting for the catalog fallback tier (#1922), in which case the
    /// candidates path must resolve the machine profile once to source it. firmware.gcodeDialect
    /// is intentionally excluded: it is sourced from firmware detection only, never from the
    /// profile (#1613 §4.5/§4.5.1).
    /// </summary>
    private static bool NeedsMachineProfileDerivation(
        Printer printer,
        List<Toolhead> physicalToolheads,
        int? activeToolheadIndex)
    {
        // #1922: this decides only whether GetCandidatesAsync's bulk listing needs to pay for a
        // machine-profile resolution. The catalog model must NOT factor into that decision: the
        // fallback order is printer ?? machineProfile ?? catalogModel, so machine-profile ranks
        // strictly above the catalog. Skipping profile derivation whenever the (lower-priority)
        // catalog happens to cover a field would let a stale/default catalog value silently win
        // over a genuine profile-derived value. Only the printer row's own completeness may
        // justify skipping the profile lookup.
        bool printerMotionTypeUnknown = printer.CalibrationMotionType is null or
            Farm.Infrastructure.Domain.CalibrationMotionType.Unknown;

        if (printer.MaxBuildVolumeX is null ||
            printer.MaxBuildVolumeY is null ||
            printer.MaxBuildVolumeZ is null ||
            printer.BedOriginX is null ||
            printer.BedOriginY is null ||
            printer.PrintablePolygonJson is null ||
            printerMotionTypeUnknown ||
            printer.MaxAcceleration is null ||
            printer.MaxTravelSpeed is null ||
            printer.CalibrationHasHeatedBed is null ||
            printer.CalibrationHasHeatedChamber is null ||
            printer.CalibrationSlicerEngine is null ||
            printer.CalibrationSlicerDistribution is null ||
            printer.CalibrationSlicerVersion is null ||
            printer.CalibrationProfileFormat is null)
        {
            return true;
        }

        Toolhead? activeToolhead = activeToolheadIndex.HasValue
            ? physicalToolheads.FirstOrDefault(
                toolhead => toolhead.Index == activeToolheadIndex.Value)
            : null;
        if (activeToolhead is null)
        {
            return false;
        }

        return activeToolhead.NozzleDiameter is null ||
            activeToolhead.NozzleType is null ||
            activeToolhead.NozzleMaxTemperature is null ||
            activeToolhead.HotendMaxTemperature is null;
    }

    private static CalibrationProfileSelection? ValidateProfileSelection(
        Printer printer,
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs)
    {
        Guid machineProfileId = printer.CalibrationMachineProfileId.GetValueOrDefault();
        Guid processProfileId = printer.CalibrationProcessProfileId.GetValueOrDefault();
        Guid filamentProfileId = printer.CalibrationFilamentProfileId.GetValueOrDefault();
        bool hasMachineProfileId = machineProfileId != Guid.Empty;
        bool hasProcessProfileId = processProfileId != Guid.Empty;
        bool hasFilamentProfileId = filamentProfileId != Guid.Empty;

        if (!hasMachineProfileId)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "machine_profile_missing",
                "slicer.machineProfileId",
                "An explicit upstream OrcaSlicer machine profile is required.");
        }

        if (!hasProcessProfileId)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "process_profile_missing",
                "slicer.processProfileId",
                "An explicit upstream OrcaSlicer process profile is required.");
        }

        if (!hasFilamentProfileId)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "filament_profile_missing",
                "slicer.filamentProfileId",
                "An explicit upstream OrcaSlicer filament profile is required.");
        }

        return hasMachineProfileId &&
               hasProcessProfileId &&
               hasFilamentProfileId
            ? new(
                machineProfileId,
                processProfileId,
                filamentProfileId)
            : null;
    }

    private Task<ResolvedCalibrationProfiles> ResolveProfilesAsync(
        CalibrationProfileSelection profileSelection,
        CalibrationProfileAccessScope profileAccessScope,
        CancellationToken cancellationToken)
    {
        ICalibrationProfileResolver resolver = profileResolver
            ?? throw new CalibrationProfileResolverUnavailableException();
        return resolver.ResolveAsync(
            profileSelection.MachineProfileId,
            profileSelection.ProcessProfileId,
            profileSelection.FilamentProfileId,
            profileAccessScope,
            cancellationToken);
    }

    private CalibrationProfileEvaluation EvaluateProfiles(
        Printer printer,
        ResolvedCalibrationProfiles resolved,
        CalibrationToolheadDto[] toolheads,
        EffectiveHardwareFacts hardwareFacts,
        int? activeToolheadIndex,
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs)
    {
        if (resolved.Machine is null)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "machine_profile_not_found",
                "slicer.machineProfileId",
                "The selected machine profile was not found.");
        }

        if (resolved.Process is null)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "process_profile_not_found",
                "slicer.processProfileId",
                "The selected process profile was not found.");
        }

        if (resolved.Filament is null)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "filament_profile_not_found",
                "slicer.filamentProfileId",
                "The selected filament profile was not found.");
        }

        ProfileState machine = ValidateProfile(
            resolved.Machine,
            "profiles.machine",
            printer,
            reasons,
            missingInputs);
        ProfileState process = ValidateProfile(
            resolved.Process,
            "profiles.process",
            printer,
            reasons,
            missingInputs);
        ProfileState filament = ValidateProfile(
            resolved.Filament,
            "profiles.filament",
            printer,
            reasons,
            missingInputs);

        if (resolved.Machine is not null)
        {
            ValidateMachineProfile(
                machine.Json,
                toolheads,
                reasons,
                missingInputs);
        }

        if (resolved.Machine is not null && resolved.Process is not null)
        {
            ValidateCompatiblePrinter(
                resolved.Process,
                resolved.Machine.Name,
                "profiles.process.compatiblePrinters",
                reasons,
                missingInputs);
        }

        if (resolved.Machine is not null && resolved.Filament is not null)
        {
            ValidateCompatiblePrinter(
                resolved.Filament,
                resolved.Machine.Name,
                "profiles.filament.compatiblePrinters",
                reasons,
                missingInputs);
        }

        CalibrationToolheadDto? activeToolheadFacts = toolheads
            .SingleOrDefault(toolhead => toolhead.Index == activeToolheadIndex);
        if (resolved.Filament is not null && activeToolheadFacts is not null)
        {
            ValidateFilamentSafety(
                resolved.Filament,
                filament.Json,
                activeToolheadFacts,
                hardwareFacts.HasHeatedBed,
                printer,
                reasons,
                missingInputs);
        }

        double? maxVolumetricFlow = activeToolheadFacts?.MaxVolumetricFlow ??
            GetFirstNumber(filament.Json, "filament_max_volumetric_speed");
        CalibrationBaselineSettingsDto baseline = new(
            activeToolheadFacts?.NozzleDiameter,
            resolved.Process?.LayerHeight,
            resolved.Process?.InfillPercentage,
            resolved.Process?.PrintSpeed,
            resolved.Filament?.NozzleTemperature,
            resolved.Filament?.BedTemperature,
            maxVolumetricFlow);

        CalibrationProfileSetDto profiles = new(
            MapProfile(resolved.Machine, machine),
            MapProfile(resolved.Process, process),
            MapProfile(resolved.Filament, filament));
        CalibrationRawEffectiveSettingsDto rawSettings = new(
            machine.Json,
            process.Json,
            filament.Json);
        IReadOnlyList<CalibrationFilamentProductChoiceDto> products =
            resolved.Filament is null
                ? []
                :
                [
                    new(
                        resolved.Filament.Id,
                        resolved.Filament.Name,
                        resolved.Filament.Material ?? string.Empty,
                        resolved.Filament.Manufacturer,
                        resolved.Filament.Sku),
                ];

        return new(profiles, baseline, rawSettings, products);
    }

    private ProfileState ValidateProfile(
        ResolvedCalibrationProfile? profile,
        string field,
        Printer printer,
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs)
    {
        if (profile is null)
        {
            return ProfileState.Empty;
        }

        if (!string.Equals(
            profile.SlicerType,
            CalibrationContractConstants.SlicerEngine,
            StringComparison.Ordinal))
        {
            Reject(
                reasons,
                missingInputs,
                "profile_slicer_mismatch",
                $"{field}.slicerType",
                "The selected profile is not an OrcaSlicer profile.");
        }

        if (string.IsNullOrWhiteSpace(profile.SlicerDistribution))
        {
            RejectMissing(
                reasons,
                missingInputs,
                "profile_distribution_missing",
                $"{field}.slicerDistribution",
                "The selected profile has no explicit slicer distribution.");
        }
        else if (!string.Equals(
            profile.SlicerDistribution,
            CalibrationContractConstants.SlicerDistribution,
            StringComparison.Ordinal) ||
            !string.Equals(
                profile.SlicerDistribution,
                printer.CalibrationSlicerDistribution,
                StringComparison.Ordinal))
        {
            Reject(
                reasons,
                missingInputs,
                "profile_distribution_unsupported",
                $"{field}.slicerDistribution",
                "The selected profile is not from the supported upstream distribution.");
        }

        if (string.IsNullOrWhiteSpace(profile.SlicerVersion))
        {
            RejectMissing(
                reasons,
                missingInputs,
                "profile_version_missing",
                $"{field}.slicerVersion",
                "The selected profile has no explicit slicer version.");
        }
        else if (!_compatibilityPolicy.IsSupported(profile.SlicerVersion))
        {
            Reject(
                reasons,
                missingInputs,
                "profile_version_mismatch",
                $"{field}.slicerVersion",
                $"The profile version is not in the configured OrcaSlicer allow-list ({string.Join(", ", _compatibilityPolicy.SupportedVersions)}).");
        }

        if (string.IsNullOrWhiteSpace(profile.ProfileFormat))
        {
            RejectMissing(
                reasons,
                missingInputs,
                "profile_format_missing",
                $"{field}.profileFormat",
                "The selected profile has no explicit profile format.");
        }
        else if (!string.Equals(
            profile.ProfileFormat,
            CalibrationContractConstants.ProfileFormat,
            StringComparison.Ordinal) ||
            !string.Equals(
                profile.ProfileFormat,
                printer.CalibrationProfileFormat,
                StringComparison.Ordinal))
        {
            Reject(
                reasons,
                missingInputs,
                "profile_format_unsupported",
                $"{field}.profileFormat",
                "The selected profile format is not supported.");
        }

        if (!profile.UpdatedAtUtc.HasValue)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "profile_revision_missing",
                $"{field}.updatedAtUtc",
                "The selected profile has no authoritative revision timestamp.");
        }

        CalibrationProfileSafetyResult safety =
            CalibrationProfileSafetyValidator.Validate(profile.RawJson, $"{field}.exactJson");
        if (!safety.IsSafe)
        {
            Reject(
                reasons,
                missingInputs,
                safety.Code!,
                safety.Field!,
                safety.Message!);
            return ProfileState.Empty;
        }

        string exactSha256 = ComputeSha256(profile.RawJson!);
        if (!string.IsNullOrWhiteSpace(profile.StoredSha256) &&
            !string.Equals(
                exactSha256,
                profile.StoredSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            Reject(
                reasons,
                missingInputs,
                "profile_hash_mismatch",
                $"{field}.sha256",
                "The persisted profile hash does not match the exact UTF-8 JSON.");
        }

        if (profile.PrinterModelId.HasValue &&
            profile.PrinterModelId.Value != printer.ModelId)
        {
            Reject(
                reasons,
                missingInputs,
                "profile_printer_model_mismatch",
                $"{field}.printerModelId",
                "The selected profile targets a different explicit printer model identifier.");
        }

        if (profile.SpecificPrinterId.HasValue &&
            profile.SpecificPrinterId.Value != printer.Id)
        {
            Reject(
                reasons,
                missingInputs,
                "profile_printer_mismatch",
                $"{field}.specificPrinterId",
                "The selected profile targets a different printer.");
        }

        return new(safety.Json, exactSha256);
    }

    private static void ValidateMachineProfile(
        JsonElement? json,
        CalibrationToolheadDto[] toolheads,
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs)
    {
        if (!json.HasValue)
        {
            return;
        }

        string[] flavors = GetStrings(json, "gcode_flavor");
        if (flavors.Length == 0)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "profile_gcode_dialect_missing",
                "profiles.machine.exactJson.gcode_flavor",
                "The machine profile does not declare a G-code dialect.");
        }
        else if (!flavors.Any(flavor =>
            string.Equals(flavor, "klipper", StringComparison.OrdinalIgnoreCase)))
        {
            Reject(
                reasons,
                missingInputs,
                "profile_gcode_dialect_mismatch",
                "profiles.machine.exactJson.gcode_flavor",
                "The machine profile is not configured for Klipper G-code.");
        }

        List<double> profileNozzles = GetNumbers(json, "nozzle_diameter");
        if (profileNozzles.Count == 0)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "profile_nozzle_data_missing",
                "profiles.machine.exactJson.nozzle_diameter",
                "The machine profile does not declare nozzle diameters.");
            return;
        }

        // #1614 AC-3: cross-validate against the effective (explicit-override-or-derived)
        // nozzle diameter, not the raw column, so a toolhead relying on profile derivation is
        // not spuriously flagged as mismatched against the very profile that supplies it.
        double[] installedNozzles = toolheads
            .Where(toolhead => toolhead.NozzleDiameter.HasValue)
            .Select(toolhead => toolhead.NozzleDiameter!.Value)
            .Order()
            .ToArray();
        double[] selectedNozzles = profileNozzles.Order().ToArray();
        if (installedNozzles.Length != toolheads.Length ||
            selectedNozzles.Length != installedNozzles.Length ||
            selectedNozzles.Where((diameter, index) =>
                Math.Abs(diameter - installedNozzles[index]) > NumericTolerance).Any())
        {
            Reject(
                reasons,
                missingInputs,
                "profile_nozzle_mismatch",
                "profiles.machine.exactJson.nozzle_diameter",
                "The machine profile nozzle layout does not match every physical toolhead.");
        }
    }

    private static void ValidateCompatiblePrinter(
        ResolvedCalibrationProfile profile,
        string machineProfileName,
        string field,
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs)
    {
        if (string.IsNullOrWhiteSpace(profile.CompatiblePrinters))
        {
            RejectMissing(
                reasons,
                missingInputs,
                "profile_compatibility_missing",
                field,
                "The selected profile has no explicit compatible machine profiles.");
            return;
        }

        bool compatible = profile.CompatiblePrinters
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(name => string.Equals(
                name,
                machineProfileName,
                StringComparison.Ordinal));
        if (!compatible)
        {
            Reject(
                reasons,
                missingInputs,
                "profile_machine_mismatch",
                field,
                "The selected profile does not explicitly reference the selected machine profile.");
        }
    }

    private static void ValidateFilamentSafety(
        ResolvedCalibrationProfile filament,
        JsonElement? json,
        CalibrationToolheadDto activeToolheadFacts,
        bool? effectiveHasHeatedBed,
        Printer printer,
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs)
    {
        if (string.IsNullOrWhiteSpace(filament.Material))
        {
            RejectMissing(
                reasons,
                missingInputs,
                "filament_material_missing",
                "profiles.filament.material",
                "The selected filament profile has no explicit material.");
        }
        else if (activeToolheadFacts.SupportedMaterials is not null &&
            !activeToolheadFacts.SupportedMaterials.Any(material =>
                string.Equals(
                    material,
                    filament.Material,
                    StringComparison.OrdinalIgnoreCase)))
        {
            Reject(
                reasons,
                missingInputs,
                "filament_material_unsupported",
                "profiles.filament.material",
                "The active toolhead does not declare support for the selected filament material.");
        }

        // #1614 AC-3: validate against the effective (explicit-override-or-derived) hotend
        // limit, not the raw column, so a toolhead relying on profile derivation is not
        // silently skipped by this safety check.
        if (filament.NozzleTemperature.HasValue &&
            activeToolheadFacts.HotendMaxTemperature.HasValue &&
            filament.NozzleTemperature.Value > activeToolheadFacts.HotendMaxTemperature.Value)
        {
            Reject(
                reasons,
                missingInputs,
                "filament_hotend_temperature_exceeds_limit",
                "profiles.filament.nozzleTemperature",
                "The filament profile temperature exceeds the installed hotend limit.");
        }

        if (filament.BedTemperature > 0 &&
            effectiveHasHeatedBed == false)
        {
            Reject(
                reasons,
                missingInputs,
                "filament_bed_temperature_requires_heated_bed",
                "profiles.filament.bedTemperature",
                "The filament profile requires a heated bed that the printer does not have.");
        }
        else if (filament.BedTemperature.HasValue &&
            effectiveHasHeatedBed == true &&
            printer.MaxBedTemp.HasValue &&
            filament.BedTemperature.Value > printer.MaxBedTemp.Value)
        {
            Reject(
                reasons,
                missingInputs,
                "filament_bed_temperature_exceeds_limit",
                "profiles.filament.bedTemperature",
                "The filament profile temperature exceeds the verified bed limit.");
        }

        double? requiredHrc = GetFirstNumber(json, "required_nozzle_HRC");
        if (requiredHrc > 0 && activeToolheadFacts.NozzleIsHardened != true)
        {
            Reject(
                reasons,
                missingInputs,
                "profile_nozzle_material_mismatch",
                "profiles.filament.exactJson.required_nozzle_HRC",
                "The filament profile requires a hardened nozzle.");
        }
    }

    private PrinterGcodeDialect ValidateFirmware(
        Printer printer,
        DateTime nowUtc,
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs)
    {
        if (printer.FirmwareFamily == PrinterFirmwareFamily.Unknown)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "firmware_family_unknown",
                "firmware.family",
                "Firmware family has not been explicitly identified.");
        }
        else if (printer.FirmwareFamily != PrinterFirmwareFamily.Klipper)
        {
            Reject(
                reasons,
                missingInputs,
                "firmware_family_not_klipper",
                "firmware.family",
                "Printer Calibration currently requires Klipper firmware.");
        }

        // firmware.gcodeDialect is sourced from firmware detection only, never from the
        // resolved machine profile (#1613 §4.5/§4.5.1): when the explicit dialect column is
        // unset, fall back to the dialect implied by the detected firmware family.
        PrinterGcodeDialect effectiveGcodeDialect = printer.GcodeDialect != PrinterGcodeDialect.Unknown
            ? printer.GcodeDialect
            : printer.FirmwareFamily == PrinterFirmwareFamily.Klipper
                ? PrinterGcodeDialect.Klipper
                : PrinterGcodeDialect.Unknown;

        if (effectiveGcodeDialect == PrinterGcodeDialect.Unknown)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "gcode_dialect_unknown",
                "firmware.gcodeDialect",
                "G-code dialect has not been explicitly identified.");
        }
        else if (effectiveGcodeDialect != PrinterGcodeDialect.Klipper)
        {
            Reject(
                reasons,
                missingInputs,
                "gcode_dialect_not_klipper",
                "firmware.gcodeDialect",
                "Printer Calibration currently requires the Klipper G-code dialect.");
        }

        if (printer.FirmwareDetectionSource == FirmwareDetectionSource.Unknown)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "firmware_detection_source_unknown",
                "firmware.detectionSource",
                "Firmware detection source is unknown.");
        }

        RequireString(
            printer.FirmwareVersion,
            "firmware_version_missing",
            "firmware.version",
            "Firmware version is required.",
            reasons,
            missingInputs);
        RequireString(
            printer.FirmwareDetectionVersion,
            "firmware_detection_version_missing",
            "firmware.detectionVersion",
            "Firmware detector or configuration version is required.",
            reasons,
            missingInputs);

        if (!printer.FirmwareDetectionConfidence.HasValue)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "firmware_detection_confidence_missing",
                "firmware.detectionConfidence",
                "Firmware detection confidence is required.");
        }
        else if (printer.FirmwareDetectionConfidence is < 0 or > 1)
        {
            Reject(
                reasons,
                missingInputs,
                "firmware_detection_confidence_invalid",
                "firmware.detectionConfidence",
                "Firmware detection confidence must be between zero and one.");
        }

        if (!printer.FirmwareIdentityVerified)
        {
            Reject(
                reasons,
                missingInputs,
                "firmware_identity_unverified",
                "firmware.verified",
                "Firmware identity has not been verified.");
        }

        if (!printer.FirmwareDetectedAtUtc.HasValue)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "firmware_detection_time_missing",
                "firmware.detectedAtUtc",
                "Firmware detection time is required.");
        }
        else if (nowUtc - NormalizeUtc(printer.FirmwareDetectedAtUtc.Value) >
            TimeSpan.FromSeconds(_firmwareStaleAfterSeconds))
        {
            Reject(
                reasons,
                missingInputs,
                "firmware_metadata_stale",
                "firmware.detectedAtUtc",
                "Firmware identity metadata is stale.");
        }

        return effectiveGcodeDialect;
    }

    private CalibrationSlicerIdentityDto ValidateSlicerIdentity(
        Printer printer,
        ResolvedCalibrationProfile? derivedMachine,
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs)
    {
        string? slicerEngine = printer.CalibrationSlicerEngine ?? derivedMachine?.SlicerType;
        string? slicerDistribution =
            printer.CalibrationSlicerDistribution ?? derivedMachine?.SlicerDistribution;
        string? slicerVersion = printer.CalibrationSlicerVersion ?? derivedMachine?.SlicerVersion;
        string? profileFormat = printer.CalibrationProfileFormat ?? derivedMachine?.ProfileFormat;

        ValidateIdentityValue(
            slicerEngine,
            CalibrationContractConstants.SlicerEngine,
            "slicer_engine_missing",
            "slicer_engine_unsupported",
            "slicer.engine",
            reasons,
            missingInputs);
        ValidateIdentityValue(
            slicerDistribution,
            CalibrationContractConstants.SlicerDistribution,
            "slicer_distribution_missing",
            "slicer_distribution_unsupported",
            "slicer.distribution",
            reasons,
            missingInputs);
        if (string.IsNullOrWhiteSpace(slicerVersion))
        {
            RejectMissing(
                reasons,
                missingInputs,
                "slicer_version_missing",
                "slicer.version",
                "slicer.version is required.");
        }
        else if (!_compatibilityPolicy.IsSupported(slicerVersion))
        {
            Reject(
                reasons,
                missingInputs,
                "slicer_version_unsupported",
                "slicer.version",
                $"slicer.version must be in the configured allow-list ({string.Join(", ", _compatibilityPolicy.SupportedVersions)}).");
        }

        ValidateIdentityValue(
            profileFormat,
            CalibrationContractConstants.ProfileFormat,
            "profile_format_missing",
            "profile_format_unsupported",
            "slicer.profileFormat",
            reasons,
            missingInputs);

        return new(
            slicerEngine,
            slicerDistribution,
            slicerVersion,
            profileFormat,
            printer.CalibrationMachineProfileId,
            printer.CalibrationProcessProfileId,
            printer.CalibrationFilamentProfileId);
    }

    private EffectiveHardwareFacts ValidateHardware(
        Printer printer,
        List<Toolhead> physicalToolheads,
        IReadOnlyList<CalibrationPointDto>? printablePolygon,
        DerivedMachineFacts derivedFacts,
        DerivedCatalogFacts catalogFacts,
        int? activeToolheadIndex,
        bool isSingleToolhead,
        bool supportsMultiExtruderStatus,
        DateTime nowUtc,
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs)
    {
        double? buildVolumeX = printer.MaxBuildVolumeX ?? derivedFacts.BuildVolumeX ?? catalogFacts.BuildVolumeX;
        double? buildVolumeY = printer.MaxBuildVolumeY ?? derivedFacts.BuildVolumeY ?? catalogFacts.BuildVolumeY;
        double? buildVolumeZ = printer.MaxBuildVolumeZ ?? derivedFacts.BuildVolumeZ ?? catalogFacts.BuildVolumeZ;
        double? bedOriginX = printer.BedOriginX ?? derivedFacts.BedOriginX ?? catalogFacts.BedOriginX;
        double? bedOriginY = printer.BedOriginY ?? derivedFacts.BedOriginY ?? catalogFacts.BedOriginY;
        bool printerMotionTypeUnknown = printer.CalibrationMotionType is null or
            Farm.Infrastructure.Domain.CalibrationMotionType.Unknown;
        bool derivedMotionTypeUnknown = derivedFacts.MotionType is null or
            Farm.Infrastructure.Domain.CalibrationMotionType.Unknown;
        Farm.Infrastructure.Domain.CalibrationMotionType? motionType =
            !printerMotionTypeUnknown
                ? printer.CalibrationMotionType
                : !derivedMotionTypeUnknown
                    ? derivedFacts.MotionType
                    : catalogFacts.MotionType;
        int? maxAcceleration = printer.MaxAcceleration ?? derivedFacts.MaxAcceleration ?? catalogFacts.MaxAcceleration;
        int? maxTravelSpeed = printer.MaxTravelSpeed ?? derivedFacts.MaxTravelSpeed;

        // maxTravelAcceleration and hasEnclosure have no machine-profile-derived tier by design
        // -- the machine profile DTO (ResolvedCalibrationProfiles.Machine) does not assert
        // either fact, so these two intentionally resolve printer ?? catalog only (#1922).
        int? maxTravelAcceleration = printer.MaxTravelAcceleration ?? catalogFacts.MaxTravelAcceleration;
        bool? hasHeatedBed = printer.CalibrationHasHeatedBed ?? derivedFacts.HasHeatedBed ?? catalogFacts.HasHeatedBed;
        bool? hasHeatedChamber = printer.CalibrationHasHeatedChamber ?? derivedFacts.HasHeatedChamber ??
            catalogFacts.HasHeatedChamber;
        bool? hasEnclosure = printer.CalibrationHasEnclosure ?? catalogFacts.HasEnclosure;

        RequirePositive(
            buildVolumeX,
            "build_volume_x_missing",
            "buildVolume.x",
            reasons,
            missingInputs);
        RequirePositive(
            buildVolumeY,
            "build_volume_y_missing",
            "buildVolume.y",
            reasons,
            missingInputs);
        RequirePositive(
            buildVolumeZ,
            "build_volume_z_missing",
            "buildVolume.z",
            reasons,
            missingInputs);
        RequireValue(
            bedOriginX,
            "bed_origin_x_missing",
            "bedOrigin.x",
            reasons,
            missingInputs);
        RequireValue(
            bedOriginY,
            "bed_origin_y_missing",
            "bedOrigin.y",
            reasons,
            missingInputs);
        if (printablePolygon is null)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "printable_polygon_missing",
                "printablePolygon",
                "Printable polygon is required.");
        }

        if (!motionType.HasValue ||
            motionType == Farm.Infrastructure.Domain.CalibrationMotionType.Unknown)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "motion_type_missing",
                "motionType",
                "Motion type is required.");
        }

        RequirePositive(
            printer.MaxPrintSpeed,
            "max_print_speed_missing",
            "maxPrintSpeed",
            reasons,
            missingInputs);
        RequirePositive(
            maxTravelSpeed,
            "max_travel_speed_missing",
            "maxTravelSpeed",
            reasons,
            missingInputs);
        RequirePositive(
            maxAcceleration,
            "max_acceleration_missing",
            "maxAcceleration",
            reasons,
            missingInputs);
        RequirePositive(
            maxTravelAcceleration,
            "max_travel_acceleration_missing",
            "maxTravelAcceleration",
            reasons,
            missingInputs);

        RequireValue(
            hasHeatedBed,
            "heated_bed_state_missing",
            "hasHeatedBed",
            reasons,
            missingInputs);
        if (hasHeatedBed == true)
        {
            RequirePositive(
                printer.MaxBedTemp,
                "max_bed_temperature_missing",
                "maxBedTemperature",
                reasons,
                missingInputs);
        }

        RequireValue(
            hasEnclosure,
            "enclosure_state_missing",
            "hasEnclosure",
            reasons,
            missingInputs);
        RequireValue(
            hasHeatedChamber,
            "heated_chamber_state_missing",
            "hasHeatedChamber",
            reasons,
            missingInputs);
        if (hasHeatedChamber == true)
        {
            RequirePositive(
                printer.MaxChamberTemp,
                "max_chamber_temperature_missing",
                "maxChamberTemperature",
                reasons,
                missingInputs);
        }

        RequireValue(
            printer.SupportsPressureAdvance,
            "pressure_advance_capability_missing",
            "supportsPressureAdvance",
            reasons,
            missingInputs);
        RequireValue(
            printer.SupportsFirmwareRetraction,
            "firmware_retraction_capability_missing",
            "supportsFirmwareRetraction",
            reasons,
            missingInputs);

        if (!printer.CalibrationHardwareVerifiedAtUtc.HasValue)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "hardware_verification_time_missing",
                "calibrationHardwareVerifiedAtUtc",
                "Hardware and safety metadata verification time is required.");
        }
        else if (nowUtc - NormalizeUtc(printer.CalibrationHardwareVerifiedAtUtc.Value) >
            TimeSpan.FromSeconds(_hardwareStaleAfterSeconds))
        {
            Reject(
                reasons,
                missingInputs,
                "hardware_metadata_stale",
                "calibrationHardwareVerifiedAtUtc",
                "Hardware and safety metadata is stale.");
        }

        if (physicalToolheads.Count == 0)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "physical_toolhead_missing",
                "toolheads",
                "At least one physical toolhead is required.");
        }

        if (!activeToolheadIndex.HasValue)
        {
            RejectMissing(
                reasons,
                missingInputs,
                "active_toolhead_missing",
                "activeToolheadIndex",
                "Active physical toolhead index is required.");
        }
        else if (!physicalToolheads.Any(toolhead =>
            toolhead.Index == activeToolheadIndex.Value))
        {
            Reject(
                reasons,
                missingInputs,
                "active_toolhead_invalid",
                "activeToolheadIndex",
                "Active toolhead index does not identify a physical toolhead.");
        }

        if (physicalToolheads.Count > 1 && !supportsMultiExtruderStatus)
        {
            Reject(
                reasons,
                missingInputs,
                "multi_extruder_status_unsupported",
                "supportsMultiExtruderStatus",
                "Multi-tool calibration requires explicit multi-extruder status support.");
        }

        foreach (Toolhead toolhead in physicalToolheads)
        {
            string field = $"toolheads[{toolhead.Index}]";
            bool isActiveToolhead = activeToolheadIndex.HasValue &&
                toolhead.Index == activeToolheadIndex.Value;
            var facts = ResolveToolheadFacts(
                toolhead, isActiveToolhead, derivedFacts, catalogFacts, isSingleToolhead);
            RequireValue(
                facts.OffsetX,
                "toolhead_offset_x_missing",
                $"{field}.offset.x",
                reasons,
                missingInputs);
            RequireValue(
                facts.OffsetY,
                "toolhead_offset_y_missing",
                $"{field}.offset.y",
                reasons,
                missingInputs);
            RequireValue(
                facts.OffsetZ,
                "toolhead_offset_z_missing",
                $"{field}.offset.z",
                reasons,
                missingInputs);
            RequirePositive(
                facts.NozzleDiameter,
                "nozzle_diameter_missing",
                $"{field}.nozzleDiameter",
                reasons,
                missingInputs);
            RequireValue(
                facts.NozzleType,
                "nozzle_type_missing",
                $"{field}.nozzleType",
                reasons,
                missingInputs);
            RequireString(
                facts.NozzleMaterial,
                "nozzle_material_missing",
                $"{field}.nozzleMaterial",
                "Nozzle material is required.",
                reasons,
                missingInputs);
            RequirePositive(
                facts.NozzleMaxTemperature,
                "nozzle_max_temperature_missing",
                $"{field}.nozzleMaxTemperature",
                reasons,
                missingInputs);
            RequireValue(
                facts.NozzleIsHardened,
                "nozzle_hardness_missing",
                $"{field}.nozzleIsHardened",
                reasons,
                missingInputs);
            RequirePositive(
                facts.HotendMaxTemperature,
                "hotend_max_temperature_missing",
                $"{field}.hotendMaxTemperature",
                reasons,
                missingInputs);
            RequirePositive(
                facts.MaxVolumetricFlow,
                "max_volumetric_flow_missing",
                $"{field}.maxVolumetricFlow",
                reasons,
                missingInputs);
            RequireString(
                facts.DriveType,
                "drive_type_missing",
                $"{field}.driveType",
                "Extruder drive type is required.",
                reasons,
                missingInputs);
            RequireValue(
                facts.IsDirectDrive,
                "direct_drive_state_missing",
                $"{field}.isDirectDrive",
                reasons,
                missingInputs);
            RequireString(
                facts.ExtruderGearRatio,
                "extruder_gear_ratio_missing",
                $"{field}.extruderGearRatio",
                "Extruder gear ratio is required.",
                reasons,
                missingInputs);
            if (facts.SupportedMaterials is null ||
                facts.SupportedMaterials.Count == 0)
            {
                RejectMissing(
                    reasons,
                    missingInputs,
                    "supported_materials_missing",
                    $"{field}.supportedMaterials",
                    "Supported materials are required.");
            }
        }

        return new(
            buildVolumeX,
            buildVolumeY,
            buildVolumeZ,
            bedOriginX,
            bedOriginY,
            motionType,
            maxAcceleration,
            maxTravelSpeed,
            maxTravelAcceleration,
            hasHeatedBed,
            hasHeatedChamber,
            hasEnclosure);
    }

    /// <summary>
    /// Coalesces a toolhead's explicit facts with the resolved machine profile's derived facts
    /// (#1613 §4.6, active toolhead only) and the printer's catalog model facts (#1922, any
    /// toolhead index): <c>printer.X ?? machineProfile.X ?? catalogModel.X ?? null</c>. Unlike
    /// the machine profile — which describes only the currently-active tool — the catalog model
    /// enumerates every toolhead position, so it applies to inactive toolheads too. Single-
    /// toolhead printers do not require manual offset entry (#1922 AC): a missing offset
    /// component defaults to zero when the printer has exactly one physical toolhead.
    /// </summary>
    private static (
        double? NozzleDiameter,
        Farm.Infrastructure.Domain.NozzleType? NozzleType,
        string? NozzleMaterial,
        int? NozzleMaxTemperature,
        bool? NozzleIsHardened,
        int? HotendMaxTemperature,
        double? MaxVolumetricFlow,
        string? DriveType,
        bool? IsDirectDrive,
        string? ExtruderGearRatio,
        IReadOnlyList<string>? SupportedMaterials,
        double? OffsetX,
        double? OffsetY,
        double? OffsetZ) ResolveToolheadFacts(
        Toolhead toolhead,
        bool isActiveToolhead,
        DerivedMachineFacts derivedFacts,
        DerivedCatalogFacts catalogFacts,
        bool isSingleToolhead)
    {
        catalogFacts.ToolheadsByIndex.TryGetValue(toolhead.Index, out CatalogToolheadFacts catalog);

        double? nozzleDiameter = toolhead.NozzleDiameter ??
            (isActiveToolhead ? derivedFacts.NozzleDiameter : null) ??
            catalog.NozzleDiameter;
        Farm.Infrastructure.Domain.NozzleType? nozzleType = toolhead.NozzleType ??
            (isActiveToolhead ? derivedFacts.NozzleType : null) ??
            catalog.NozzleType;
        int? nozzleMaxTemperature = toolhead.NozzleMaxTemperature ??
            (isActiveToolhead ? derivedFacts.NozzleMaxTemperature : null) ??
            catalog.NozzleMaxTemperature;
        int? hotendMaxTemperature = toolhead.HotendMaxTemperature ??
            (isActiveToolhead ? derivedFacts.HotendMaxTemperature : null) ??
            catalog.HotendMaxTemperature;

        double? offsetX = toolhead.OffsetX ?? (isSingleToolhead ? 0 : null);
        double? offsetY = toolhead.OffsetY ?? (isSingleToolhead ? 0 : null);
        double? offsetZ = toolhead.OffsetZ ?? (isSingleToolhead ? 0 : null);

        return (
            nozzleDiameter,
            nozzleType,
            toolhead.NozzleMaterial ?? catalog.NozzleMaterial,
            nozzleMaxTemperature,
            toolhead.NozzleIsHardened ?? catalog.NozzleIsHardened,
            hotendMaxTemperature,
            toolhead.MaxVolumetricFlow ?? catalog.MaxVolumetricFlow,
            toolhead.DriveType ?? catalog.DriveType,
            toolhead.IsDirectDrive ?? catalog.IsDirectDrive,
            toolhead.ExtruderGearRatio ?? catalog.ExtruderGearRatio,
            toolhead.SupportedMaterials ?? catalog.SupportedMaterials,
            offsetX,
            offsetY,
            offsetZ);
    }

    private static CalibrationToolheadDto MapToolhead(
        Toolhead toolhead,
        int? activeToolheadIndex,
        DerivedMachineFacts derivedFacts,
        DerivedCatalogFacts catalogFacts,
        bool isSingleToolhead)
    {
        bool isActiveToolhead = activeToolheadIndex.HasValue &&
            toolhead.Index == activeToolheadIndex.Value;
        var facts = ResolveToolheadFacts(
            toolhead, isActiveToolhead, derivedFacts, catalogFacts, isSingleToolhead);
        return new(
            toolhead.Id,
            toolhead.Index,
            toolhead.Name,
            toolhead.IsPrimary,
            new(facts.OffsetX, facts.OffsetY, facts.OffsetZ),
            facts.NozzleDiameter,
            facts.NozzleType?.ToString(),
            facts.NozzleMaterial,
            facts.NozzleMaxTemperature,
            facts.NozzleIsHardened,
            facts.HotendMaxTemperature,
            facts.MaxVolumetricFlow,
            facts.DriveType,
            facts.IsDirectDrive,
            facts.ExtruderGearRatio,
            facts.SupportedMaterials);
    }

    private static CalibrationProfileDto? MapProfile(
        ResolvedCalibrationProfile? profile,
        ProfileState state)
    {
        if (profile is null)
        {
            return null;
        }

        string revision = profile.UpdatedAtUtc?.Ticks.ToString(
            CultureInfo.InvariantCulture) ?? "unknown";
        return new(
            profile.Id,
            profile.Kind,
            profile.Name,
            profile.SlicerType,
            profile.SlicerDistribution,
            profile.SlicerVersion,
            profile.ProfileFormat,
            revision,
            profile.UpdatedAtUtc,
            state.Json.HasValue ? profile.RawJson : null,
            state.ExactSha256);
    }

    private static CalibrationProfileHashInput? CreateProfileHashInput(
        CalibrationProfileDto? profile,
        JsonElement? effectiveSettings) =>
        profile is null
            ? null
            : new(
                profile.Id,
                profile.Kind,
                profile.Name,
                profile.SlicerType,
                profile.SlicerDistribution,
                profile.SlicerVersion,
                profile.ProfileFormat,
                profile.ProfileRevision,
                profile.UpdatedAtUtc,
                effectiveSettings);

    private static List<string> GetSupportedCalibrationMethods(Printer printer)
    {
        List<string> methods =
        [
            "temperature",
            "flow_rate",
            "max_volumetric_speed",
            "vfa",
        ];
        if (printer.SupportsPressureAdvance == true)
        {
            methods.Add("pressure_advance");
        }

        if (printer.SupportsFirmwareRetraction == true)
        {
            methods.Add("retraction");
        }

        return methods;
    }

    private static string NormalizeOperationalState(PrinterStatusDto? status)
    {
        if (status is null)
        {
            return "unknown";
        }

        if (!status.IsOnline)
        {
            return "offline";
        }

        string state = status.State?.Trim().ToLowerInvariant() ?? string.Empty;
        return state switch
        {
            "idle" or "ready" or "standby" or "operational" => "idle",
            "printing" or "running" => "printing",
            "paused" => "paused",
            "error" or "shutdown" or "fault" => "error",
            "" or "unknown" => "unknown",
            _ => "busy",
        };
    }

    private static T[]? ParseJsonList<T>(
        string? rawJson,
        string field,
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs)
    {
        if (rawJson is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T[]>(rawJson);
        }
        catch (JsonException)
        {
            Reject(
                reasons,
                missingInputs,
                "geometry_json_invalid",
                field,
                "Stored geometry JSON is invalid.");
            return null;
        }
    }

    private static string[] GetStrings(
        JsonElement? root,
        string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out JsonElement value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray();
        }

        return value.ValueKind == JsonValueKind.String
            ? [value.GetString()!]
            : [];
    }

    private static List<double> GetNumbers(
        JsonElement? root,
        string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out JsonElement value))
        {
            return [];
        }

        IEnumerable<JsonElement> values = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [value];
        List<double> numbers = [];
        foreach (JsonElement item in values.Where(item => TryReadNumber(item, out _)))
        {
            _ = TryReadNumber(item, out double number);
            numbers.Add(number);
        }

        return numbers;
    }

    private static bool TryReadNumber(JsonElement item, out double number)
    {
        if (item.ValueKind == JsonValueKind.Number)
        {
            return item.TryGetDouble(out number);
        }

        if (item.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(
                item.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number);
        }

        number = 0;
        return false;
    }

    private static double? GetFirstNumber(
        JsonElement? root,
        string propertyName)
    {
        List<double> numbers = GetNumbers(root, propertyName);
        return numbers.Count > 0 && numbers[0] > 0 ? numbers[0] : null;
    }

    private static bool TryGetProperty(
        JsonElement? root,
        string propertyName,
        out JsonElement value)
    {
        JsonElement? match = root is { ValueKind: JsonValueKind.Object }
            ? root.Value.EnumerateObject()
                .Where(property => string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                .Select(property => (JsonElement?)property.Value)
                .FirstOrDefault()
            : null;

        value = match ?? default;
        return match.HasValue;
    }

    private static string ComputeSha256(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ValidateIdentityValue(
        string? actual,
        string expected,
        string missingCode,
        string mismatchCode,
        string field,
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            RejectMissing(
                reasons,
                missingInputs,
                missingCode,
                field,
                $"{field} is required.");
        }
        else if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            Reject(
                reasons,
                missingInputs,
                mismatchCode,
                field,
                $"{field} must be '{expected}'.");
        }
    }

    private static void RequireString(
        string? value,
        string code,
        string field,
        string message,
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            RejectMissing(reasons, missingInputs, code, field, message);
        }
    }

    private static void RequirePositive<T>(
        T? value,
        string code,
        string field,
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs)
        where T : struct, IComparable<T>
    {
        if (!value.HasValue || value.Value.CompareTo(default) <= 0)
        {
            RejectMissing(
                reasons,
                missingInputs,
                code,
                field,
                $"{field} must be explicitly supplied and greater than zero.");
        }
    }

    private static void RequireValue<T>(
        T? value,
        string code,
        string field,
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs)
        where T : struct
    {
        if (!value.HasValue)
        {
            RejectMissing(
                reasons,
                missingInputs,
                code,
                field,
                $"{field} must be explicitly supplied.");
        }
    }

    private static void RejectMissing(
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs,
        string code,
        string field,
        string message)
    {
        _ = missingInputs.Add(field);
        Reject(reasons, missingInputs, code, field, message);
    }

    private static void Reject(
        List<CalibrationRejectionReasonDto> reasons,
        HashSet<string> missingInputs,
        string code,
        string field,
        string message)
    {
        ArgumentNullException.ThrowIfNull(missingInputs);
        if (!reasons.Any(reason =>
            string.Equals(reason.Code, code, StringComparison.Ordinal) &&
            string.Equals(reason.Field, field, StringComparison.Ordinal)))
        {
            reasons.Add(new(code, field, message));
        }
    }

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private sealed record CalibrationEvaluation(
        CalibrationCandidateDto Candidate,
        CalibrationProfileSetDto Profiles,
        CalibrationBaselineSettingsDto BaselineSettings,
        CalibrationRawEffectiveSettingsDto RawEffectiveSettings,
        IReadOnlyList<CalibrationFilamentProductChoiceDto> FilamentProducts);

    private sealed record CalibrationProfileSelection(
        Guid MachineProfileId,
        Guid ProcessProfileId,
        Guid FilamentProfileId);

    /// <summary>
    /// The effective (explicit-override-or-profile-derived) hardware facts produced by
    /// <see cref="ValidateHardware"/>, reused for the final <see cref="CalibrationCandidateDto"/>
    /// so validated and reported values never diverge (#1613 §4.2/§4.3).
    /// </summary>
    private readonly record struct EffectiveHardwareFacts(
        double? BuildVolumeX,
        double? BuildVolumeY,
        double? BuildVolumeZ,
        double? BedOriginX,
        double? BedOriginY,
        Farm.Infrastructure.Domain.CalibrationMotionType? MotionType,
        int? MaxAcceleration,
        int? MaxTravelSpeed,
        int? MaxTravelAcceleration,
        bool? HasHeatedBed,
        bool? HasHeatedChamber,
        bool? HasEnclosure);

    private sealed record CalibrationProfileEvaluation(
        CalibrationProfileSetDto Profiles,
        CalibrationBaselineSettingsDto BaselineSettings,
        CalibrationRawEffectiveSettingsDto RawEffectiveSettings,
        IReadOnlyList<CalibrationFilamentProductChoiceDto> FilamentProducts)
    {
        public static CalibrationProfileEvaluation Empty { get; } = new(
            new(null, null, null),
            new(null, null, null, null, null, null, null),
            new(null, null, null),
            []);
    }

    private sealed record ProfileState(JsonElement? Json, string? ExactSha256)
    {
        public static ProfileState Empty { get; } = new(null, null);
    }

    private sealed record CalibrationProfileHashInput(
        Guid Id,
        string Kind,
        string Name,
        string SlicerType,
        string? SlicerDistribution,
        string? SlicerVersion,
        string? ProfileFormat,
        string ProfileRevision,
        DateTime? UpdatedAtUtc,
        JsonElement? EffectiveSettings);
}
