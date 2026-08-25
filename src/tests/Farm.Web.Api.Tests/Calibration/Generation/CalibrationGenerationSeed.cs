using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Tests.Calibration.Generation;

/// <summary>
/// Seeds a complete, generation-ready calibration aggregate into any core context.
/// </summary>
/// <remarks>
/// The attempt is stored with the specification the production compiler actually produces for the
/// seeded snapshot, which is what lets the saga's exact-match verification succeed without the seed
/// ever rewriting or relaxing it.
/// </remarks>
internal static class CalibrationGenerationSeed
{
    /// <summary>The pinned container digest a healthy attested worker publishes.</summary>
    public const string ContainerDigest =
        "sha256:0f5c6a6f1b1c4a1cbb2b0f1a1e8c2b1e3f4a5b6c7d8e9f0a1b2c3d4e5f6a7b8c";

    /// <summary>The pinned binary digest a healthy attested worker publishes.</summary>
    public const string BinaryDigest =
        "9f2c1b0a8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a";

    /// <summary>The stable operation identifier the attempt aggregate creates its orchestration with.</summary>
    public const string AttemptOperationId = "attempt-operation-0001";

    /// <summary>Exact native machine profile stored on the immutable snapshot.</summary>
    public const string MachineProfileJson =
        """{"name":"PF Machine","printer_technology":"FFF","nozzle_diameter":["0.4"],"printable_area":["0x0","235x0","235x235","0x235"],"retraction_length":["0.8"]}""";

    /// <summary>Exact native process profile stored on the immutable snapshot.</summary>
    public const string ProcessProfileJson =
        """{"name":"PF Process","layer_height":"0.2","line_width":"0.45","wall_loops":"2"}""";

    /// <summary>Exact native filament profile stored on the immutable snapshot.</summary>
    public const string FilamentProfileJson =
        """{"name":"PF Filament","filament_type":["PLA"],"filament_flow_ratio":["1"],"nozzle_temperature":["220"],"filament_max_volumetric_speed":["18"]}""";

    /// <summary>The exact native profile documents an immutable snapshot is seeded with.</summary>
    /// <param name="MachineJson">Exact native machine document.</param>
    /// <param name="ProcessJson">Exact native process document.</param>
    /// <param name="FilamentJson">Exact native filament document.</param>
    /// <param name="NozzleDiameterMillimeters">Nozzle the machine document declares.</param>
    /// <remarks>
    /// The pinned-worker smoke replaces the canonical documents with the ones the published container
    /// actually publishes, so the slicer receives its own native profiles back. Every other caller keeps
    /// <see cref="Canonical"/>, which is byte-identical to what this seed has always produced.
    /// </remarks>
    public sealed record ProfileSet(
        string MachineJson,
        string ProcessJson,
        string FilamentJson,
        double NozzleDiameterMillimeters)
    {
        /// <summary>The canonical documents every non-smoke generation test seeds with.</summary>
        public static ProfileSet Canonical { get; } = new(
            MachineProfileJson,
            ProcessProfileJson,
            FilamentProfileJson,
            0.4);
    }

    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Seeds project, printer, snapshot, attempt and orchestration rows.</summary>
    /// <param name="coreFactory">Creates a core context bound to the target database.</param>
    /// <param name="method">Canonical calibration method name.</param>
    /// <param name="ownerId">The owning user.</param>
    /// <param name="tamperSpecification">Stores a specification the recompile cannot reproduce.</param>
    /// <param name="profiles">Exact native profiles to store, or <see langword="null"/> for the canonical set.</param>
    /// <param name="importedAsset">Authoritative uploaded asset for final-verification attempts.</param>
    /// <param name="pinnedIdentity">Exact worker identity to compile into the immutable specification.</param>
    /// <returns>The seeded fixture.</returns>
    public static async Task<CalibrationGenerationFixture> SeedAsync(
        Func<AppDbContext> coreFactory,
        string method,
        Guid ownerId,
        bool tamperSpecification,
        ProfileSet? profiles = null,
        CalibrationModelReference? importedAsset = null,
        CalibrationPinnedSlicerIdentity? pinnedIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(coreFactory);
        ProfileSet profileSet = profiles ?? ProfileSet.Canonical;
        CalibrationPinnedSlicerIdentity pinned = pinnedIdentity ?? new(
            CalibrationContractConstants.SlicerVersion,
            CalibrationContractConstants.SlicerDistribution,
            ContainerDigest,
            BinaryDigest,
            Guid.NewGuid());

        Guid projectId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        Guid orchestrationId = Guid.NewGuid();
        Guid snapshotId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid toolheadId = Guid.NewGuid();
        Guid machineProfileId = Guid.NewGuid();
        Guid processProfileId = Guid.NewGuid();
        Guid filamentProfileId = Guid.NewGuid();
        DateTime nowUtc = DateTime.UtcNow;

        PrinterConfigurationSnapshotDto document = BuildSnapshotDocument(
            printerId,
            toolheadId,
            machineProfileId,
            processProfileId,
            filamentProfileId,
            nowUtc,
            snapshotSha256: string.Empty,
            profileSet);
        string snapshotSha256 = CalibrationCanonicalJson.ComputeSha256(document);
        document = BuildSnapshotDocument(
            printerId,
            toolheadId,
            machineProfileId,
            processProfileId,
            filamentProfileId,
            nowUtc,
            snapshotSha256,
            profileSet);

        await using (AppDbContext core = coreFactory())
        {
            Guid manufacturerId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            _ = core.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"M-{manufacturerId:N}" });
            _ = core.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                ManufacturerId = manufacturerId,
                Name = $"Model-{modelId:N}",
            });
            _ = core.Printers.Add(new Printer
            {
                Id = printerId,
                Name = $"Printer-{printerId:N}",
                ServerUrl = $"http://{printerId:N}.test",
                BackendPort = 7125,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
                ConfigurationRevision = 42,
            });
            _ = core.CalibrationProjects.Add(new CalibrationProject
            {
                Id = projectId,
                OwnerUserId = ownerId,
                Name = "Generation project",
                PrinterId = printerId,
                SelectedToolheadId = toolheadId,
                SelectedToolheadIndex = 0,
                FilamentProvider = "catalog",
                FilamentProductId = $"product-{projectId:N}",
                FilamentProductName = "PLA",
                FilamentMaterial = "PLA",
                FilamentDiameter = 1.75m,
                FilamentSnapshotJson = "{}",
                OrderedStepsJson = "[]",
                CurrentSelectionsJson = "{}",
                CreateRequestId = $"seed-{projectId:N}",
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
                CreatedBySubject = "seed",
                UpdatedBySubject = "seed",
            });
            _ = await core.SaveChangesAsync();
        }

        SeededSnapshot snapshot = new(
            snapshotId,
            printerId,
            machineProfileId,
            processProfileId,
            filamentProfileId,
            profileSet.MachineJson,
            CalibrationCanonicalJson.ComputeTextSha256(profileSet.MachineJson),
            profileSet.ProcessJson,
            CalibrationCanonicalJson.ComputeTextSha256(profileSet.ProcessJson),
            profileSet.FilamentJson,
            CalibrationCanonicalJson.ComputeTextSha256(profileSet.FilamentJson),
            snapshotSha256,
            42,
            nowUtc,
            pinned);

        CalibrationMethodOptionsRequest options = new() { Model3DId = importedAsset?.Model3DId };
        CalibrationSpecification specification = await CompileSpecificationAsync(
            coreFactory,
            projectId,
            attemptId,
            orchestrationId,
            document,
            snapshot,
            method,
            options,
            importedAsset,
            pinned);

        await using (AppDbContext core = coreFactory())
        {
            _ = core.CalibrationAttempts.Add(new CalibrationAttempt
            {
                Id = attemptId,
                ProjectId = projectId,
                Sequence = 1,
                CalibrationKind = specification.Document.CalibrationKind,
                Method = method,
                DefinitionVersion = CalibrationMethodOptions.CurrentDefinitionVersion,
                InputJson = JsonSerializer.Serialize(options, SnapshotOptions),
                SpecificationJson = tamperSpecification
                    ? """{"schemaVersion":"9.9"}"""
                    : specification.CanonicalJson,
                SpecificationSha256 = tamperSpecification
                    ? CalibrationCanonicalJson.ComputeTextSha256("tampered")
                    : specification.Sha256,
                PrinterConfigurationSnapshotId = snapshotId,
                ProfileSnapshotIdsJson = "[]",
                AttemptRequestId = AttemptOperationId,
                CreatedAtUtc = nowUtc,
                CreatedBySubject = "seed",
            });
            _ = core.CalibrationOrchestrations.Add(new CalibrationOrchestration
            {
                Id = orchestrationId,
                ProjectId = projectId,
                AttemptId = attemptId,
                CurrentStep = CalibrationGenerationSteps.Created,
                Status = CalibrationOrchestrationStatus.Pending,
                OperationId = AttemptOperationId,
                Revision = 1,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            });
            _ = await core.SaveChangesAsync();
        }

        return new CalibrationGenerationFixture(
            projectId,
            attemptId,
            orchestrationId,
            printerId,
            snapshotId,
            method,
            options,
            specification,
            new CalibrationActor(ownerId, $"owner-{ownerId:N}", false));
    }

    /// <summary>Builds the capabilities document an attested pinned worker registers with.</summary>
    /// <param name="containerDigest">Container digest, or <see langword="null"/> to omit it.</param>
    /// <param name="binaryDigest">Binary digest, or <see langword="null"/> to omit it.</param>
    /// <param name="version">Exact upstream slicer version attested by the worker.</param>
    /// <returns>The capabilities JSON.</returns>
    public static string BuildAttestationJson(
        string? containerDigest = ContainerDigest,
        string? binaryDigest = BinaryDigest,
        string version = CalibrationContractConstants.SlicerVersion) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["capabilities"] = new[] { "orcaslicer", CalibrationContractConstants.UpstreamSlicerCapability },
            ["engineVersion"] = version,
            ["slicerDistribution"] = CalibrationContractConstants.SlicerDistribution,
            ["slicerVersion"] = version,
            [CalibrationSlicerAttestation.ContainerDigestProperty] = containerDigest,
            [CalibrationSlicerAttestation.BinaryDigestProperty] = binaryDigest,
            ["realBinary"] = true,
        });

    private static async Task<CalibrationSpecification> CompileSpecificationAsync(
        Func<AppDbContext> coreFactory,
        Guid projectId,
        Guid attemptId,
        Guid orchestrationId,
        PrinterConfigurationSnapshotDto document,
        SeededSnapshot snapshot,
        string method,
        CalibrationMethodOptionsRequest options,
        CalibrationModelReference? importedAsset,
        CalibrationPinnedSlicerIdentity pinned)
    {
        await using AppDbContext core = coreFactory();
        CalibrationProject project = await core.CalibrationProjects
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == projectId);

        CalibrationGenerationResult<CalibrationMethodOptions> bound =
            CalibrationMethodOptionsBinder.Bind(
                method,
                CalibrationMethodOptions.CurrentDefinitionVersion,
                options);
        CalibrationGenerationContext context = BuildContext(
            project,
            attemptId,
            orchestrationId,
            document,
            snapshot,
            importedAsset);
        CalibrationGenerationResult<CalibrationSpecification> compiled =
            new CalibrationSpecificationCompiler(
                TimeProvider.System,
                new CalibrationSlicerCompatibilityPolicy([pinned.Version]))
                .Compile(context, bound.Value!);
        return compiled.Value ?? throw new InvalidOperationException(
            "The seed could not compile a valid calibration specification: " +
            string.Join(", ", compiled.Problems.Select(problem => $"{problem.Code}@{problem.Field}")));
    }

    /// <summary>
    /// The in-memory equivalent of the now-deleted immutable printer configuration snapshot row.
    /// </summary>
    /// <remarks>
    /// D3 (#1980) deleted the <c>PrinterConfigurationSnapshot</c> entity and its table. Generation
    /// tests still need a full <see cref="CalibrationGenerationContext"/> to exercise the deterministic
    /// compiler, so this seed rebuilds that context directly from an in-memory document rather than a
    /// persisted snapshot row.
    /// </remarks>
    private sealed record SeededSnapshot(
        Guid Id,
        Guid PrinterId,
        Guid MachineProfileId,
        Guid ProcessProfileId,
        Guid FilamentProfileId,
        string ExactMachineProfileJson,
        string MachineProfileSha256,
        string ExactProcessProfileJson,
        string ProcessProfileSha256,
        string ExactFilamentProfileJson,
        string FilamentProfileSha256,
        string SnapshotSha256,
        long PrinterConfigurationRevision,
        DateTime CapturedAtUtc,
        CalibrationPinnedSlicerIdentity Pinned);

    /// <summary>Rebuilds the authoritative generation context from the seeded document and snapshot.</summary>
    private static CalibrationGenerationContext BuildContext(
        CalibrationProject project,
        Guid attemptId,
        Guid orchestrationId,
        PrinterConfigurationSnapshotDto document,
        SeededSnapshot snapshot,
        CalibrationModelReference? importedAsset)
    {
        CalibrationToolheadDto toolhead = SelectToolhead(project, document)
            ?? throw new InvalidOperationException("The seeded document does not describe the selected toolhead.");
        DateTime capturedAtUtc = DateTime.SpecifyKind(snapshot.CapturedAtUtc, DateTimeKind.Utc);
        return new CalibrationGenerationContext
        {
            ProjectId = project.Id,
            AttemptId = attemptId,
            OrchestrationId = orchestrationId,
            PrinterId = snapshot.PrinterId,
            PrinterConfigurationSnapshotId = snapshot.Id,
            PrinterConfigurationRevision = snapshot.PrinterConfigurationRevision,
            PrinterConfigurationSnapshotSha256 = snapshot.SnapshotSha256,
            CurrentPrinterConfigurationRevision = snapshot.PrinterConfigurationRevision,
            SnapshotCapturedAtUtc = capturedAtUtc,
            Compatibility = new CalibrationCompatibilityIdentity(
                PrinterFirmwareFamily.Klipper.ToString(),
                PrinterGcodeDialect.Klipper.ToString(),
                Blank(CalibrationContractConstants.SlicerEngine),
                Blank(CalibrationContractConstants.SlicerDistribution),
                snapshot.Pinned.Version,
                snapshot.Pinned.ContainerDigest,
                snapshot.Pinned.BinarySha256,
                document.Profiles.Machine?.ProfileFormat ?? CalibrationContractConstants.ProfileFormat),
            Firmware = new CalibrationFirmwareContext(
                PrinterFirmwareFamily.Klipper.ToString(),
                "v0.12.0-321",
                document.Firmware.DetectionSource,
                PrinterGcodeDialect.Klipper.ToString(),
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
                Profile(snapshot.MachineProfileId, document.Profiles.Machine, snapshot.ExactMachineProfileJson, snapshot.MachineProfileSha256),
                Profile(snapshot.ProcessProfileId, document.Profiles.Process, snapshot.ExactProcessProfileJson, snapshot.ProcessProfileSha256),
                Profile(snapshot.FilamentProfileId, document.Profiles.Filament, snapshot.ExactFilamentProfileJson, snapshot.FilamentProfileSha256)),
            Generator = CalibrationGeneratorIdentity.Current,
            OperationId = AttemptOperationId,
            ImportedAsset = importedAsset,
        };
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
        SeededSnapshot snapshot)
    {
        CalibrationFilamentProductChoiceDto? product = document.FilamentProducts
            .FirstOrDefault(candidate => candidate.ProfileId == snapshot.FilamentProfileId);
        return new CalibrationFilamentContext(
            snapshot.FilamentProfileId,
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

    private static CalibrationExactProfile Profile(
        Guid id,
        CalibrationProfileDto? described,
        string exactJson,
        string sha256) =>
        new(id, described?.Kind ?? string.Empty, described?.Name, described?.ProfileRevision, exactJson, sha256);

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

    private static PrinterConfigurationSnapshotDto BuildSnapshotDocument(
        Guid printerId,
        Guid toolheadId,
        Guid machineProfileId,
        Guid processProfileId,
        Guid filamentProfileId,
        DateTime capturedAtUtc,
        string snapshotSha256,
        ProfileSet profiles) =>
        new()
        {
            SchemaVersion = CalibrationContractConstants.SchemaVersion,
            PrinterId = printerId,
            ConfigurationRevision = 42,
            CapturedAtUtc = capturedAtUtc,
            CapturedBySubject = "seed",
            BuildVolume = new CalibrationBuildVolumeDto(235, 235, 250),
            BedOrigin = new CalibrationBedOriginDto(0, 0),
            PrintablePolygon = null,
            ExcludedRegions = null,
            MotionType = "cartesian",
            Toolheads =
            [
                new CalibrationToolheadDto(
                    toolheadId,
                    0,
                    "Primary",
                    true,
                    new CalibrationPoint3DDto(0, 0, 0),
                    profiles.NozzleDiameterMillimeters,
                    "brass",
                    "brass",
                    300,
                    false,
                    300,
                    24,
                    "direct",
                    true,
                    null,
                    null),
            ],
            HasHeatedBed = true,
            MaxBedTemperature = 120,
            HasEnclosure = false,
            HasHeatedChamber = false,
            MaxChamberTemperature = null,
            MaxPrintSpeed = 300,
            MaxTravelSpeed = 500,
            MaxAcceleration = 10000,
            MaxTravelAcceleration = 12000,
            Firmware = new CalibrationFirmwareIdentityDto(
                "Klipper",
                "Klipper",
                "printer",
                "v0.12.0-321",
                "v0.12.0-321",
                1m,
                capturedAtUtc,
                true),
            Slicer = new CalibrationSlicerIdentityDto(
                CalibrationContractConstants.SlicerEngine,
                CalibrationContractConstants.SlicerDistribution,
                CalibrationContractConstants.SlicerVersion,
                CalibrationContractConstants.ProfileFormat),
            Profiles = new CalibrationProfileSetDto(
                Profile(machineProfileId, "machine", "PF Machine", profiles.MachineJson, capturedAtUtc),
                Profile(processProfileId, "process", "PF Process", profiles.ProcessJson, capturedAtUtc),
                Profile(filamentProfileId, "filament", "PF Filament", profiles.FilamentJson, capturedAtUtc)),
            BaselineSettings = new CalibrationBaselineSettingsDto(
                profiles.NozzleDiameterMillimeters,
                0.2,
                15,
                120,
                220,
                60,
                18),
            RawEffectiveSettings = new CalibrationRawEffectiveSettingsDto(null, null, null),
            FilamentProducts =
            [
                new CalibrationFilamentProductChoiceDto(
                    filamentProfileId,
                    "PF Filament",
                    "PLA",
                    "PrintFarmer",
                    "PF-PLA-001"),
            ],
            PhysicalSpools = [],
            SnapshotSha256 = snapshotSha256,
        };

    private static CalibrationProfileDto Profile(
        Guid id,
        string kind,
        string name,
        string exactJson,
        DateTime updatedAtUtc) =>
        new(
            id,
            kind,
            name,
            CalibrationContractConstants.SlicerEngine,
            CalibrationContractConstants.SlicerDistribution,
            CalibrationContractConstants.SlicerVersion,
            CalibrationContractConstants.ProfileFormat,
            "1",
            updatedAtUtc,
            exactJson,
            CalibrationCanonicalJson.ComputeTextSha256(exactJson));
}
