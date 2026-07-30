using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Web.Api.Tests.Services.Calibration.Generation;

/// <summary>
/// Tests the profile patch exporter against the real authoritative generated profile history.
/// </summary>
public sealed class CalibrationProfilePatchExporterTests
{
    private static readonly CalibrationActor Actor =
        new(new Guid("dddddddd-dddd-dddd-dddd-dddddddddddd"), "owner", false);

    [Fact]
    public async Task ExportAsync_WithSelectedFlowRatio_ProducesNormalizedTypedPatch()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProfilePatchExporter exporter = await CreateExporterAsync(db);
        CalibrationSpecification specification = Specification();

        CalibrationGenerationResult<CalibrationProfilePatchExport> result =
            await exporter.ExportAsync(
                specification,
                new CalibrationObservationSelection(
                    CalibrationGenerationTestData.ObservationId,
                    CalibrationPatchParameter.FlowRatio,
                    0.965m,
                    CalibrationUnits.Ratio),
                "PLA calibrated flow",
                "export-0001",
                Actor,
                CancellationToken.None);

        _ = result.Problems.Should().BeEmpty();
        CalibrationProfilePatch patch = result.Value!.Patch;
        _ = patch.ProfileType.Should().Be("filament");
        _ = patch.Entries.Should().ContainSingle();
        _ = patch.Entries[0].NativeKey.Should().Be("filament_flow_ratio");
        _ = patch.Entries[0].Value.Should().Be("0.965");
        _ = patch.Entries[0].Unit.Should().Be(CalibrationUnits.Ratio);
        _ = patch.Entries[0].BaselineValue.Should().Be("1");
        _ = result.Value.PatchSha256.Should()
            .Be(CalibrationCanonicalJson.ComputeTextSha256(result.Value.PatchJson));
    }

    [Fact]
    public async Task ExportAsync_RecordsBaselineProfileHashesAndGeneratorVersions()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProfilePatchExporter exporter = await CreateExporterAsync(db);
        CalibrationSpecification specification = Specification();

        CalibrationGenerationResult<CalibrationProfilePatchExport> result =
            await exporter.ExportAsync(
                specification,
                new CalibrationObservationSelection(
                    CalibrationGenerationTestData.ObservationId,
                    CalibrationPatchParameter.FlowRatio,
                    0.98m,
                    CalibrationUnits.Ratio),
                "PLA calibrated flow",
                "export-0002",
                Actor,
                CancellationToken.None);

        _ = result.Problems.Should().BeEmpty();
        CalibrationProfilePatch patch = result.Value!.Patch;
        _ = patch.BaselineFilamentProfileSha256.Should()
            .Be(specification.Document.Profiles.Filament!.Sha256);
        _ = patch.BaselineMachineProfileSha256.Should()
            .Be(specification.Document.Profiles.Machine!.Sha256);
        _ = patch.BaselineProcessProfileSha256.Should()
            .Be(specification.Document.Profiles.Process!.Sha256);
        _ = patch.GeneratorVersion.Should().Be(CalibrationGeneratorIdentity.Current.Version);
        _ = patch.SchemaVersion.Should().Be(CalibrationProfilePatchExporter.PatchSchemaVersion);
        _ = patch.SlicerVersion.Should().Be("2.3.1");
        _ = patch.SlicerContainerDigest.Should()
            .Be(CalibrationGenerationTestData.ContainerDigest);
    }

    [Fact]
    public async Task ExportAsync_ProducesExactUpstreamJsonArtifactWithOnlyTheTunedKeyChanged()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProfilePatchExporter exporter = await CreateExporterAsync(db);
        CalibrationSpecification specification = Specification();

        CalibrationGenerationResult<CalibrationProfilePatchExport> result =
            await exporter.ExportAsync(
                specification,
                new CalibrationObservationSelection(
                    CalibrationGenerationTestData.ObservationId,
                    CalibrationPatchParameter.FlowRatio,
                    0.95m,
                    CalibrationUnits.Ratio),
                "PLA calibrated flow",
                "export-0003",
                Actor,
                CancellationToken.None);

        _ = result.Problems.Should().BeEmpty();
        string exported = result.Value!.ExactProfileJson;
        _ = exported.Should().Contain("\"filament_flow_ratio\":[\"0.95\"]");
        _ = exported.Should().Contain("\"nozzle_temperature\":[\"220\"]");
        _ = exported.Should().Contain("\"name\":\"PLA calibrated flow\"");
        _ = result.Value.ExactProfileSha256.Should()
            .Be(CalibrationCanonicalJson.ComputeTextSha256(exported));
    }

    [Fact]
    public async Task ExportAsync_PersistsThroughTheAuthoritativeGeneratedProfileHistory()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProfilePatchExporter exporter = await CreateExporterAsync(db);
        CalibrationSpecification specification = Specification();

        CalibrationGenerationResult<CalibrationProfilePatchExport> result =
            await exporter.ExportAsync(
                specification,
                new CalibrationObservationSelection(
                    CalibrationGenerationTestData.ObservationId,
                    CalibrationPatchParameter.PressureAdvance,
                    0.042m,
                    CalibrationUnits.Seconds),
                "PLA calibrated pressure advance",
                "export-0004",
                Actor,
                CancellationToken.None);

        _ = result.Problems.Should().BeEmpty();
        GeneratedProfileRevision persisted = await db.GeneratedProfileRevisions.SingleAsync();
        _ = persisted.ProjectId.Should().Be(CalibrationGenerationTestData.ProjectId);
        _ = persisted.SourceAttemptId.Should().Be(CalibrationGenerationTestData.AttemptId);
        _ = persisted.RevisionNumber.Should().Be(1);
        _ = persisted.SlicerEngine.Should().Be("OrcaSlicer");
        _ = persisted.SlicerDistribution.Should().Be("upstream");
        _ = persisted.PressureAdvance.Should().Be(0.042m);
        _ = result.Value!.Revision.Id.Should().Be(persisted.Id);
    }

    [Fact]
    public async Task ExportAsync_NeverMutatesTheBaselineProfileDocuments()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProfilePatchExporter exporter = await CreateExporterAsync(db);
        CalibrationSpecification specification = Specification();
        string baselineFilamentJson = specification.Document.Profiles.Filament!.ExactJson!;
        string baselineFilamentSha256 = specification.Document.Profiles.Filament.Sha256!;

        _ = await exporter.ExportAsync(
            specification,
            new CalibrationObservationSelection(
                CalibrationGenerationTestData.ObservationId,
                CalibrationPatchParameter.FlowRatio,
                0.97m,
                CalibrationUnits.Ratio),
            "PLA calibrated flow",
            "export-0005",
            Actor,
            CancellationToken.None);

        _ = specification.Document.Profiles.Filament.ExactJson.Should().Be(baselineFilamentJson);
        _ = specification.Document.Profiles.Filament.Sha256.Should().Be(baselineFilamentSha256);
        _ = (await db.GeneratedProfileRevisionOperations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExportAsync_WithIdenticalRequest_ReplaysTheSameRevisionWithoutDuplicating()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProfilePatchExporter exporter = await CreateExporterAsync(db);
        CalibrationSpecification specification = Specification();
        CalibrationObservationSelection selection = new(
            CalibrationGenerationTestData.ObservationId,
            CalibrationPatchParameter.FlowRatio,
            0.99m,
            CalibrationUnits.Ratio);

        CalibrationGenerationResult<CalibrationProfilePatchExport> first =
            await exporter.ExportAsync(
                specification,
                selection,
                "PLA calibrated flow",
                "export-0006",
                Actor,
                CancellationToken.None);
        CalibrationGenerationResult<CalibrationProfilePatchExport> replay =
            await exporter.ExportAsync(
                specification,
                selection,
                "PLA calibrated flow",
                "export-0006",
                Actor,
                CancellationToken.None);

        _ = first.Problems.Should().BeEmpty();
        _ = replay.Problems.Should().BeEmpty();
        _ = replay.Value!.Revision.Id.Should().Be(first.Value!.Revision.Id);
        _ = (await db.GeneratedProfileRevisions.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(CalibrationPatchParameter.FlowRatio, 3.0)]
    [InlineData(CalibrationPatchParameter.NozzleTemperature, 450)]
    [InlineData(CalibrationPatchParameter.PressureAdvance, 1.9)]
    [InlineData(CalibrationPatchParameter.RetractionLength, 9.0)]
    [InlineData(CalibrationPatchParameter.MaximumVolumetricSpeed, 90)]
    public async Task ExportAsync_WithValueOutsideTheSafeRange_RejectsWithoutPersisting(
        CalibrationPatchParameter parameter,
        double value)
    {
        await using AppDbContext db = CreateContext();
        CalibrationProfilePatchExporter exporter = await CreateExporterAsync(db);

        CalibrationGenerationResult<CalibrationProfilePatchExport> result =
            await exporter.ExportAsync(
                Specification(),
                new CalibrationObservationSelection(
                    CalibrationGenerationTestData.ObservationId,
                    parameter,
                    (decimal)value,
                    CalibrationUnits.Ratio),
                "Unsafe export",
                "export-0007",
                Actor,
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("patch_value_out_of_range");
        _ = (await db.GeneratedProfileRevisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExportAsync_WithUnsupportedParameter_RejectsWithoutPersisting()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProfilePatchExporter exporter = await CreateExporterAsync(db);

        CalibrationGenerationResult<CalibrationProfilePatchExport> result =
            await exporter.ExportAsync(
                Specification(),
                new CalibrationObservationSelection(
                    CalibrationGenerationTestData.ObservationId,
                    CalibrationPatchParameter.Unspecified,
                    1m,
                    CalibrationUnits.Ratio),
                "Unsupported export",
                "export-0008",
                Actor,
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("patch_observation_unsupported");
        _ = (await db.GeneratedProfileRevisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExportAsync_WithUnsupportedTuple_RejectsWithoutPersisting()
    {
        await using AppDbContext db = CreateContext();
        CalibrationProfilePatchExporter exporter = await CreateExporterAsync(db);
        CalibrationSpecification specification = Specification();
        CalibrationSpecificationDocument tampered = specification.Document with
        {
            Compatibility = specification.Document.Compatibility with
            {
                SlicerDistribution = "vendor-fork",
            },
        };

        CalibrationGenerationResult<CalibrationProfilePatchExport> result =
            await exporter.ExportAsync(
                new CalibrationSpecification(
                    tampered,
                    specification.CanonicalJson,
                    specification.Sha256),
                new CalibrationObservationSelection(
                    CalibrationGenerationTestData.ObservationId,
                    CalibrationPatchParameter.FlowRatio,
                    1.0m,
                    CalibrationUnits.Ratio),
                "Rejected export",
                "export-0009",
                Actor,
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("slicer_distribution_unsupported");
        _ = (await db.GeneratedProfileRevisions.CountAsync()).Should().Be(0);
    }

    private static CalibrationSpecification Specification() =>
        CalibrationGenerationPipeline
            .CompileSpecification(CalibrationMethod.FlowRatioCoarse)
            .Value!;

    private static AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"calibration-patch-export-{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<CalibrationProfilePatchExporter> CreateExporterAsync(AppDbContext db)
    {
        _ = db.CalibrationProjects.Add(new CalibrationProject
        {
            Id = CalibrationGenerationTestData.ProjectId,
            OwnerUserId = Actor.UserId,
            Name = "PLA baseline",
            PrinterId = CalibrationGenerationTestData.PrinterId,
            FilamentProvider = "catalog",
            FilamentProductId = "sku-pla-blue",
            FilamentProductName = "PLA Blue",
            FilamentMaterial = "PLA",
            CreateRequestId = "seed-project",
            CreatedAtUtc = CalibrationGenerationTestData.CapturedAtUtc,
            UpdatedAtUtc = CalibrationGenerationTestData.CapturedAtUtc,
            CreatedBySubject = Actor.Subject,
            UpdatedBySubject = Actor.Subject,
        });
        _ = db.PrinterConfigurationSnapshots.Add(new PrinterConfigurationSnapshot
        {
            Id = CalibrationGenerationTestData.SnapshotId,
            ProjectId = CalibrationGenerationTestData.ProjectId,
            PrinterId = CalibrationGenerationTestData.PrinterId,
            SchemaVersion = CalibrationContractConstants.SchemaVersion,
            SanitizedSnapshotJson = "{}",
            SnapshotSha256 = new string('5', 64),
            PrinterConfigurationRevision = 42,
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            FirmwareDetectionSource = FirmwareDetectionSource.Printer,
            SlicerEngine = CalibrationContractConstants.SlicerEngine,
            SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
            SlicerVersion = CalibrationContractConstants.SlicerVersion,
            SlicerContainerDigest = CalibrationGenerationTestData.ContainerDigest,
            CapturedAtUtc = CalibrationGenerationTestData.CapturedAtUtc,
            CapturedBySubject = Actor.Subject,
        });
        _ = db.CalibrationAttempts.Add(new CalibrationAttempt
        {
            Id = CalibrationGenerationTestData.AttemptId,
            ProjectId = CalibrationGenerationTestData.ProjectId,
            Sequence = 1,
            CalibrationKind = "flow",
            Method = CalibrationMethodNames.FlowRatioCoarse,
            DefinitionVersion = "1.0",
            InputJson = "{}",
            SpecificationJson = "{}",
            SpecificationSha256 = new string('0', 64),
            PrinterConfigurationSnapshotId = CalibrationGenerationTestData.SnapshotId,
            ProfileSnapshotIdsJson = "[]",
            AttemptRequestId = "seed-attempt",
            CreatedAtUtc = CalibrationGenerationTestData.CapturedAtUtc,
            CreatedBySubject = Actor.Subject,
        });
        _ = await db.SaveChangesAsync(CancellationToken.None);

        CalibrationProjectService service = new(
            db,
            new StubPrinterCalibrationContextService(),
            new StubCalibrationBlobStore(),
            TimeProvider.System,
            NullLogger<CalibrationProjectService>.Instance);
        return new CalibrationProfilePatchExporter(service);
    }

    private sealed class StubPrinterCalibrationContextService : IPrinterCalibrationContextService
    {
        public Task<CalibrationServiceResult<IReadOnlyList<CalibrationCandidateDto>>>
            GetCandidatesAsync(
                CalibrationProfileAccessScope profileAccessScope,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                new CalibrationServiceResult<IReadOnlyList<CalibrationCandidateDto>>([]));

        public Task<CalibrationServiceResult<CalibrationContextDto>> GetContextAsync(
            Guid printerId,
            long? configurationRevision,
            string capturedBySubject,
            CalibrationProfileAccessScope profileAccessScope,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CalibrationServiceResult<CalibrationContextDto>(
                null,
                "printer_not_found"));
    }

    private sealed class StubCalibrationBlobStore : ICalibrationBlobStore
    {
        public Task DeleteAsync(string opaqueStorageKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> ExistsAsync(string opaqueStorageKey, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<CalibrationBlobMetadata?> GetMetadataAsync(
            string opaqueStorageKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<CalibrationBlobMetadata?>(null);

        public Task<Stream> OpenReadAsync(
            string opaqueStorageKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<CalibrationBlobMetadata> PutAsync(
            CalibrationBlobWriteRequest request,
            Stream content,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "Profile patch export never writes a calibration blob.");
    }
}
