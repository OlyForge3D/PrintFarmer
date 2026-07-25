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
    /// <returns>The seeded fixture.</returns>
    public static async Task<CalibrationGenerationFixture> SeedAsync(
        Func<AppDbContext> coreFactory,
        string method,
        Guid ownerId,
        bool tamperSpecification)
    {
        ArgumentNullException.ThrowIfNull(coreFactory);

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
            snapshotSha256: string.Empty);
        string snapshotSha256 = CalibrationCanonicalJson.ComputeSha256(document);
        document = BuildSnapshotDocument(
            printerId,
            toolheadId,
            machineProfileId,
            processProfileId,
            filamentProfileId,
            nowUtc,
            snapshotSha256);

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
            _ = core.PrinterConfigurationSnapshots.Add(new PrinterConfigurationSnapshot
            {
                Id = snapshotId,
                ProjectId = projectId,
                AttemptId = attemptId,
                PrinterId = printerId,
                SchemaVersion = CalibrationContractConstants.SchemaVersion,
                SanitizedSnapshotJson = JsonSerializer.Serialize(document, SnapshotOptions),
                SnapshotSha256 = snapshotSha256,
                PrinterConfigurationRevision = 42,
                FirmwareFamily = PrinterFirmwareFamily.Klipper,
                GcodeDialect = PrinterGcodeDialect.Klipper,
                FirmwareDetectionSource = FirmwareDetectionSource.Printer,
                FirmwareVersion = "v0.12.0-321",
                SlicerEngine = CalibrationContractConstants.SlicerEngine,
                SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                SlicerVersion = CalibrationContractConstants.SlicerVersion,
                SlicerContainerDigest = ContainerDigest,
                MachineProfileId = machineProfileId,
                ExactMachineProfileJson = MachineProfileJson,
                MachineProfileSha256 = CalibrationCanonicalJson.ComputeTextSha256(MachineProfileJson),
                ProcessProfileId = processProfileId,
                ExactProcessProfileJson = ProcessProfileJson,
                ProcessProfileSha256 = CalibrationCanonicalJson.ComputeTextSha256(ProcessProfileJson),
                FilamentProfileId = filamentProfileId,
                ExactFilamentProfileJson = FilamentProfileJson,
                FilamentProfileSha256 = CalibrationCanonicalJson.ComputeTextSha256(FilamentProfileJson),
                CapturedAtUtc = nowUtc,
                CapturedBySubject = "seed",
            });
            _ = await core.SaveChangesAsync();
        }

        CalibrationMethodOptionsRequest options = new();
        CalibrationSpecification specification = await CompileSpecificationAsync(
            coreFactory,
            projectId,
            attemptId,
            orchestrationId,
            snapshotId,
            method,
            options);

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
    /// <returns>The capabilities JSON.</returns>
    public static string BuildAttestationJson(
        string? containerDigest = ContainerDigest,
        string? binaryDigest = BinaryDigest) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["capabilities"] = new[] { "orcaslicer", CalibrationContractConstants.UpstreamSlicerCapability },
            [CalibrationSlicerAttestation.ContainerDigestProperty] = containerDigest,
            [CalibrationSlicerAttestation.BinaryDigestProperty] = binaryDigest,
        });

    private static async Task<CalibrationSpecification> CompileSpecificationAsync(
        Func<AppDbContext> coreFactory,
        Guid projectId,
        Guid attemptId,
        Guid orchestrationId,
        Guid snapshotId,
        string method,
        CalibrationMethodOptionsRequest options)
    {
        await using AppDbContext core = coreFactory();
        CalibrationProject project = await core.CalibrationProjects
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == projectId);
        PrinterConfigurationSnapshot snapshot = await core.PrinterConfigurationSnapshots
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == snapshotId);

        CalibrationGenerationResult<CalibrationMethodOptions> bound =
            CalibrationMethodOptionsBinder.Bind(
                method,
                CalibrationMethodOptions.CurrentDefinitionVersion,
                options);
        CalibrationGenerationResult<CalibrationGenerationContext> context =
            CalibrationGenerationContextFactory.Build(
                project,
                new CalibrationAttempt { Id = attemptId, ProjectId = projectId },
                new CalibrationOrchestration
                {
                    Id = orchestrationId,
                    ProjectId = projectId,
                    AttemptId = attemptId,
                    OperationId = AttemptOperationId,
                },
                snapshot,
                snapshot.PrinterConfigurationRevision,
                new CalibrationPinnedSlicerIdentity(
                    CalibrationContractConstants.SlicerVersion,
                    CalibrationContractConstants.SlicerDistribution,
                    ContainerDigest,
                    BinaryDigest,
                    Guid.NewGuid()),
                importedAsset: null);
        CalibrationGenerationResult<CalibrationSpecification> compiled =
            new CalibrationSpecificationCompiler(TimeProvider.System)
                .Compile(context.Value!, bound.Value!);
        return compiled.Value ?? throw new InvalidOperationException(
            "The seed could not compile a valid calibration specification: " +
            string.Join(", ", compiled.Problems.Select(problem => $"{problem.Code}@{problem.Field}")));
    }

    private static PrinterConfigurationSnapshotDto BuildSnapshotDocument(
        Guid printerId,
        Guid toolheadId,
        Guid machineProfileId,
        Guid processProfileId,
        Guid filamentProfileId,
        DateTime capturedAtUtc,
        string snapshotSha256) =>
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
                    0.4,
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
                Profile(machineProfileId, "machine", "PF Machine", MachineProfileJson, capturedAtUtc),
                Profile(processProfileId, "process", "PF Process", ProcessProfileJson, capturedAtUtc),
                Profile(filamentProfileId, "filament", "PF Filament", FilamentProfileJson, capturedAtUtc)),
            BaselineSettings = new CalibrationBaselineSettingsDto(0.4, 0.2, 15, 120, 220, 60, 18),
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
