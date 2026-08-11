using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Services.Calibration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Farm.Web.Api.Tests.Services.Calibration;

public sealed class PrinterCalibrationContextServiceTests
{
    private static readonly CalibrationProfileAccessScope ProfileAccess =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), BypassOwnership: false);

    [Fact]
    public async Task GetCandidatesAsync_WithCompleteVerifiedConfiguration_ReturnsEligibleCandidate()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();

        CalibrationServiceResult<IReadOnlyList<CalibrationCandidateDto>> result =
            await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None);

        _ = result.ErrorCode.Should().BeNull();
        CalibrationCandidateDto candidate = result.Value.Should().ContainSingle().Which;
        _ = candidate.Eligible.Should().BeTrue();
        _ = candidate.RejectionReasons.Should().BeEmpty();
        _ = candidate.Firmware.Family.Should().Be("Klipper");
        _ = candidate.Firmware.GcodeDialect.Should().Be("Klipper");
        _ = candidate.Slicer.Engine.Should().Be("OrcaSlicer");
        _ = candidate.Slicer.Distribution.Should().Be("upstream");
        _ = candidate.SupportsStatus.Should().BeTrue();
        _ = candidate.SupportsDirectCommand.Should().BeTrue();
        _ = candidate.SupportsMultiExtruderStatus.Should().BeFalse();
    }

    [Fact]
    public async Task GetCandidatesAsync_WithPrintablePolygonPointMissingCoordinate_ReturnsTypedGeometryReason()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.PrintablePolygonJson =
            """[{"x":0,"y":0},{"x":250},{"x":250,"y":250}]""";
        _ = await harness.Db.SaveChangesAsync();

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.PrintablePolygon.Should().BeNull();
        _ = candidate.RejectionReasons.Should().ContainSingle(reason =>
            reason.Code == "geometry_json_invalid" &&
            reason.Field == "printablePolygon");
    }

    [Fact]
    public async Task GetCandidatesAsync_WithExcludedRegionPointMissingCoordinate_ReturnsTypedGeometryReason()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.ExcludedRegionsJson =
            """[{"name":"unsafe","polygon":[{"x":10,"y":10},{"y":20},{"x":20,"y":20}]}]""";
        _ = await harness.Db.SaveChangesAsync();

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.ExcludedRegions.Should().BeNull();
        _ = candidate.RejectionReasons.Should().ContainSingle(reason =>
            reason.Code == "geometry_json_invalid" &&
            reason.Field == "excludedRegions");
    }

    [Theory]
    [InlineData(PrinterBackend.Moonraker)]
    [InlineData(PrinterBackend.OctoPrint)]
    public async Task GetCandidatesAsync_WithKlipperNamedNetworkBackendButUnknownIdentity_DoesNotInferEligibility(
        PrinterBackend backend)
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.Name = "Klipper printer alias";
        harness.Printer.Backend = (int)backend;
        harness.Printer.FirmwareFamily = PrinterFirmwareFamily.Unknown;
        harness.Printer.GcodeDialect = PrinterGcodeDialect.Unknown;
        harness.Printer.FirmwareDetectionSource = FirmwareDetectionSource.Unknown;
        _ = await harness.Db.SaveChangesAsync();

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Select(reason => reason.Code).Should().Contain(
            "firmware_family_unknown",
            "gcode_dialect_unknown",
            "firmware_detection_source_unknown");
    }

    [Fact]
    public async Task GetCandidatesAsync_WithNonKlipperOrNonUpstreamIdentity_ReturnsTypedReasons()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.FirmwareFamily = PrinterFirmwareFamily.Other;
        harness.Printer.GcodeDialect = PrinterGcodeDialect.Other;
        harness.Printer.CalibrationSlicerEngine = "PrusaSlicer";
        harness.Printer.CalibrationSlicerDistribution = "vendor-fork";
        _ = await harness.Db.SaveChangesAsync();

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Select(reason => reason.Code).Should().Contain(
            "firmware_family_not_klipper",
            "gcode_dialect_not_klipper",
            "slicer_engine_unsupported",
            "slicer_distribution_unsupported");
    }

    [Fact]
    public async Task GetCandidatesAsync_WithStaleUnverifiedFirmware_ReturnsTypedReasons()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.FirmwareIdentityVerified = false;
        harness.Printer.FirmwareDetectedAtUtc =
            harness.Now.UtcDateTime.AddDays(-2);
        _ = await harness.Db.SaveChangesAsync();

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Select(reason => reason.Code).Should().Contain(
            "firmware_identity_unverified",
            "firmware_metadata_stale");
    }

    [Fact]
    public async Task GetCandidatesAsync_WithMissingAndStaleSafetyMetadata_ReturnsTypedReasons()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.CalibrationHardwareVerifiedAtUtc =
            harness.Now.UtcDateTime.AddDays(-31);
        harness.Printer.SupportsPressureAdvance = null;
        harness.Printer.Toolheads.Single().NozzleMaterial = null;
        _ = await harness.Db.SaveChangesAsync();

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Select(reason => reason.Code).Should().Contain(
            "hardware_metadata_stale",
            "pressure_advance_capability_missing",
            "nozzle_material_missing");
        _ = candidate.MissingInputs.Should().Contain(
            "supportsPressureAdvance",
            "toolheads[0].nozzleMaterial");
    }

    [Theory]
    [InlineData("unknown", "status_unknown")]
    [InlineData("stale", "status_stale")]
    [InlineData("offline", "printer_offline")]
    [InlineData("unsupported", "status_unsupported")]
    public async Task GetCandidatesAsync_WithUnsafeStatus_ReturnsTypedReason(
        string statusCase,
        string expectedCode)
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Status = statusCase switch
        {
            "unknown" => null,
            "stale" => harness.CreateStatus(
                isOnline: true,
                state: "idle",
                observedAtUtc: harness.Now.UtcDateTime.AddMinutes(-2)),
            "offline" => harness.CreateStatus(
                isOnline: false,
                state: "offline",
                observedAtUtc: harness.Now.UtcDateTime),
            "unsupported" => harness.Status,
            _ => throw new InvalidOperationException($"Unknown status case '{statusCase}'."),
        };
        if (statusCase == "unsupported")
        {
            harness.Capabilities &= ~BackendCapabilities.Status;
        }

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        if (statusCase == "stale")
        {
            CalibrationContextDto context =
                (await harness.Service.GetContextAsync(
                    harness.Printer.Id,
                    configurationRevision: null,
                    capturedBySubject: "test-subject",
                    profileAccessScope: ProfileAccess,
                    cancellationToken: CancellationToken.None))
                .Value ?? throw new InvalidOperationException("Missing calibration context.");
            _ = context.CapturedAtUtc.Should().Be(harness.Now.UtcDateTime);
            _ = candidate.IsStale.Should().BeTrue(
                "the status observed at {0:O} is older than {1:O} with a {2}-second threshold",
                candidate.ObservedAtUtc,
                harness.Now.UtcDateTime,
                candidate.StaleAfterSeconds);
        }

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Select(reason => reason.Code)
            .Should().Contain(expectedCode);
    }

    [Fact]
    public async Task GetCandidatesAsync_WithMmuGate_ExcludesVirtualGateFromHardwareValidation()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        _ = harness.Db.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = harness.Printer.Id,
            Name = "MMU gate",
            Index = 1,
            ToolheadType = ToolheadType.MmuGate,
        });
        _ = await harness.Db.SaveChangesAsync();

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeTrue();
        _ = candidate.PhysicalToolheadCount.Should().Be(1);
        _ = candidate.Toolheads.Should().ContainSingle()
            .Which.Index.Should().Be(0);
        _ = candidate.RejectionReasons.Should().NotContain(reason =>
            reason.Field.StartsWith("toolheads[1]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetContextAsync_WithProfileMismatches_ReturnsTypedReasons()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine! with
            {
                RawJson = """{"gcode_flavor":"klipper","nozzle_diameter":[0.6]}""",
                StoredSha256 = new string('0', 64),
            },
            Filament = harness.Profiles.Filament! with
            {
                NozzleTemperature = 400,
            },
        };

        CalibrationContextDto candidate = await harness.GetContextAsync();

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Select(reason => reason.Code).Should().Contain(
            "profile_hash_mismatch",
            "profile_nozzle_mismatch",
            "filament_hotend_temperature_exceeds_limit");
    }

    [Fact]
    public async Task GetCandidatesAsync_WithUnavailableProfileStore_ReturnsCandidatesWithoutResolverCalls()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.MakeProfileResolverUnavailable();

        CalibrationServiceResult<IReadOnlyList<CalibrationCandidateDto>> result =
            await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None);

        _ = result.ErrorCode.Should().BeNull();
        _ = result.Value.Should().ContainSingle();
        harness.VerifyNoProfileResolverCalls();
    }

    [Fact]
    public async Task GetCandidatesAsync_WithMultiplePrinters_MakesZeroProfileResolverCalls()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        await harness.AddPrinterAsync("Alpha printer");
        await harness.AddPrinterAsync("Zulu printer");

        CalibrationServiceResult<IReadOnlyList<CalibrationCandidateDto>> result =
            await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None);

        _ = result.Value.Should().NotBeNull();
        _ = result.Value!.Select(candidate => candidate.Name).Should().Equal(
            "Alpha printer",
            "Explicit Klipper printer",
            "Zulu printer");
        harness.VerifyNoProfileResolverCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetCandidatesAsync_WithMissingOrEmptyProfileIds_ReturnsTypedReasonsWithoutResolverCalls(
        bool useEmptyIds)
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        Guid? profileId = useEmptyIds ? Guid.Empty : null;
        harness.Printer.CalibrationMachineProfileId = profileId;
        harness.Printer.CalibrationProcessProfileId = profileId;
        harness.Printer.CalibrationFilamentProfileId = profileId;
        _ = await harness.Db.SaveChangesAsync();

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Select(reason => reason.Code).Should().Contain(
            "machine_profile_missing",
            "process_profile_missing",
            "filament_profile_missing");
        harness.VerifyNoProfileResolverCalls();
    }

    [Fact]
    public async Task GetContextAsync_WithSelectedPrinter_ResolvesProfilesExactlyOnce()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        await harness.AddPrinterAsync("Unselected printer");

        CalibrationContextDto context = await harness.GetContextAsync();

        _ = context.Id.Should().Be(harness.Printer.Id);
        harness.VerifySingleProfileResolution();
    }

    [Fact]
    public async Task GetContextAsync_WithChangedRevision_DoesNotResolveProfiles()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();

        CalibrationServiceResult<CalibrationContextDto> result =
            await harness.Service.GetContextAsync(
                harness.Printer.Id,
                harness.Printer.ConfigurationRevision + 1,
                "test-subject",
                ProfileAccess,
                CancellationToken.None);

        _ = result.ErrorCode.Should().Be("printer_configuration_changed");
        _ = result.CurrentConfigurationRevision.Should()
            .Be(harness.Printer.ConfigurationRevision);
        harness.VerifyNoProfileResolverCalls();
    }

    [Fact]
    public async Task GetContextAsync_WithUnavailableProfileStore_ReturnsStableServiceError()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.MakeProfileResolverUnavailable();

        CalibrationServiceResult<CalibrationContextDto> result =
            await harness.Service.GetContextAsync(
                harness.Printer.Id,
                configurationRevision: null,
                capturedBySubject: "test-subject",
                profileAccessScope: ProfileAccess,
                cancellationToken: CancellationToken.None);

        _ = result.Value.Should().BeNull();
        _ = result.ErrorCode.Should().Be("profile_service_unavailable");
        harness.VerifySingleProfileResolution();
    }

    [Theory]
    [InlineData("profile_service_authentication_failed")]
    [InlineData("profile_service_authorization_failed")]
    [InlineData("profile_service_configuration_error")]
    [InlineData("profile_service_timeout")]
    public async Task GetContextAsync_WithTypedResolverFailure_PreservesFailureCode(
        string errorCode)
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.MakeProfileResolverUnavailable(errorCode);

        CalibrationServiceResult<CalibrationContextDto> result =
            await harness.Service.GetContextAsync(
                harness.Printer.Id,
                configurationRevision: null,
                capturedBySubject: "test-subject",
                profileAccessScope: ProfileAccess,
                cancellationToken: CancellationToken.None);

        _ = result.Value.Should().BeNull();
        _ = result.ErrorCode.Should().Be(errorCode);
        harness.VerifySingleProfileResolution();
    }

    [Theory]
    [InlineData(null, "orca-json", "profile_distribution_missing")]
    [InlineData("vendor-fork", "orca-json", "profile_distribution_unsupported")]
    [InlineData("upstream", null, "profile_format_missing")]
    [InlineData("upstream", "vendor-json", "profile_format_unsupported")]
    public async Task GetContextAsync_WithUnverifiedProfileIdentity_ReturnsTypedReason(
        string? distribution,
        string? profileFormat,
        string expectedCode)
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine! with
            {
                SlicerDistribution = distribution,
                ProfileFormat = profileFormat,
            },
        };

        CalibrationContextDto candidate = await harness.GetContextAsync();

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Select(reason => reason.Code)
            .Should().Contain(expectedCode);
    }

    [Fact]
    public async Task GetContextAsync_WithHeatedBedProfileForColdBed_ReturnsTypedReason()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.CalibrationHasHeatedBed = false;
        harness.Printer.MaxBedTemp = null;
        _ = await harness.Db.SaveChangesAsync();

        CalibrationContextDto candidate = await harness.GetContextAsync();

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Select(reason => reason.Code)
            .Should().Contain("filament_bed_temperature_requires_heated_bed");
    }

    [Fact]
    public async Task GetContextAsync_WithUnsupportedFilamentMaterial_ReturnsTypedReason()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.Toolheads.Single().SupportedMaterials = ["PETG"];
        _ = await harness.Db.SaveChangesAsync();

        CalibrationContextDto candidate = await harness.GetContextAsync();

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Select(reason => reason.Code)
            .Should().Contain("filament_material_unsupported");
    }

    [Fact]
    public async Task GetContextAsync_WithCredentialBearingProfile_RedactsProfileAndReturnsTypedReason()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine! with
            {
                RawJson =
                    """{"gcode_flavor":"klipper","nozzle_diameter":[0.4],"api_key":"secret-value"}""",
            },
        };

        CalibrationContextDto context =
            (await harness.Service.GetContextAsync(
                harness.Printer.Id,
                configurationRevision: null,
                capturedBySubject: "test-subject",
                profileAccessScope: ProfileAccess,
                cancellationToken: CancellationToken.None))
            .Value ?? throw new InvalidOperationException("Missing calibration context.");

        _ = context.Eligible.Should().BeFalse();
        _ = context.RejectionReasons.Select(reason => reason.Code)
            .Should().Contain("profile_contains_credential");
        _ = context.Snapshot.Profiles.Machine.Should().NotBeNull();
        _ = context.Snapshot.Profiles.Machine!.ExactJson.Should().BeNull();
        _ = context.Snapshot.RawEffectiveSettings.Machine.Should().BeNull();
        _ = context.Snapshot.ToString().Should().NotContain("secret-value");
    }

    [Theory]
    [InlineData(
        """{"gcode_flavor":"klipper","nozzle_diameter":[0.4],"token":"secret-value"}""",
        "profile_contains_credential")]
    [InlineData(
        """{"gcode_flavor":"klipper","nozzle_diameter":[0.4],"service_url":"http://10.0.0.42/api"}""",
        "profile_contains_private_url")]
    [InlineData(
        """{"gcode_flavor":"klipper","nozzle_diameter":[0.4],"script":"/opt/printfarmer/hook.sh"}""",
        "profile_contains_filesystem_path")]
    [InlineData(
        """{"gcode_flavor":"klipper","nozzle_diameter":[0.4],"post_process":"curl https://example.com"}""",
        "profile_contains_unsafe_command")]
    public async Task GetContextAsync_WithUnsafeProfilePayload_RedactsExactJsonAndReturnsTypedReason(
        string rawJson,
        string expectedCode)
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine! with { RawJson = rawJson },
        };

        CalibrationContextDto context =
            (await harness.Service.GetContextAsync(
                harness.Printer.Id,
                configurationRevision: null,
                capturedBySubject: "test-subject",
                profileAccessScope: ProfileAccess,
                cancellationToken: CancellationToken.None))
            .Value ?? throw new InvalidOperationException("Missing calibration context.");

        _ = context.Eligible.Should().BeFalse();
        _ = context.RejectionReasons.Select(reason => reason.Code)
            .Should().Contain(expectedCode);
        _ = context.Snapshot.Profiles.Machine!.ExactJson.Should().BeNull();
        _ = context.Snapshot.RawEffectiveSettings.Machine.Should().BeNull();
        _ = context.Snapshot.ToString().Should().NotContain("secret-value");
        _ = context.Snapshot.ToString().Should().NotContain("10.0.0.42");
        _ = context.Snapshot.ToString().Should().NotContain("/opt/printfarmer");
    }

    [Fact]
    public async Task GetContextAsync_WithEquivalentProfileJsonAndChangedStatus_PreservesSnapshotHash()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();

        CalibrationContextDto first =
            (await harness.Service.GetContextAsync(
                harness.Printer.Id,
                configurationRevision: null,
                capturedBySubject: "test-subject",
                profileAccessScope: ProfileAccess,
                cancellationToken: CancellationToken.None))
            .Value ?? throw new InvalidOperationException("Missing calibration context.");

        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine! with
            {
                RawJson = """{"nozzle_diameter":[0.4],"gcode_flavor":"klipper"}""",
            },
            Process = harness.Profiles.Process! with
            {
                RawJson = """{"infill_density":2e1,"layer_height":0.20}""",
            },
        };
        harness.Status = harness.CreateStatus(
            isOnline: true,
            state: "printing",
            observedAtUtc: harness.Now.UtcDateTime);

        CalibrationContextDto second =
            (await harness.Service.GetContextAsync(
                harness.Printer.Id,
                configurationRevision: null,
                capturedBySubject: "test-subject",
                profileAccessScope: ProfileAccess,
                cancellationToken: CancellationToken.None))
            .Value ?? throw new InvalidOperationException("Missing calibration context.");

        _ = second.SnapshotSha256.Should().Be(first.SnapshotSha256);
        _ = second.Snapshot.Profiles.Machine!.Sha256
            .Should().NotBe(first.Snapshot.Profiles.Machine!.Sha256);
    }

    private sealed class CalibrationHarness : IAsyncDisposable
    {
        private readonly Mock<IPrinterStatusSnapshotReader> _statusReader = new();
        private readonly Mock<IBackendCapabilityFactory> _capabilityFactory = new();
        private readonly Mock<ICalibrationProfileResolver> _profileResolver = new();

        private CalibrationHarness(AppDbContext db, Printer printer)
        {
            Db = db;
            Printer = printer;
            Now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            Capabilities =
                BackendCapabilities.Status |
                BackendCapabilities.FileUpload |
                BackendCapabilities.StartPrint |
                BackendCapabilities.DirectCommand;
            Status = CreateStatus(
                isOnline: true,
                state: "idle",
                observedAtUtc: Now.UtcDateTime);
            Profiles = CreateProfiles(printer, Now.UtcDateTime.AddHours(-1));

            _ = _statusReader
                .Setup(reader => reader.GetStatusSnapshot(It.IsAny<Guid>()))
                .Returns((Guid id) => id == Printer.Id ? Status : null);
            _ = _capabilityFactory
                .Setup(factory => factory.GetSupportedCapabilities(It.IsAny<PrinterBackend>()))
                .Returns((PrinterBackend _) => Capabilities);
            _ = _profileResolver
                .Setup(resolver => resolver.IsAvailableAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            _ = _profileResolver
                .Setup(resolver => resolver.ResolveAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<Guid>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CalibrationProfileAccessScope>(),
                    It.IsAny<CancellationToken>()))
                .Returns((
                    Guid _,
                    Guid _,
                    Guid _,
                    CalibrationProfileAccessScope _,
                    CancellationToken _) => Task.FromResult(Profiles));

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Calibration:StatusStaleAfterSeconds"] = "30",
                    ["Calibration:FirmwareMetadataStaleAfterSeconds"] = "86400",
                    ["Calibration:HardwareMetadataStaleAfterSeconds"] = "2592000",
                })
                .Build();
            Service = new PrinterCalibrationContextService(
                Db,
                _statusReader.Object,
                _capabilityFactory.Object,
                configuration,
                new FixedTimeProvider(Now),
                _profileResolver.Object);
        }

        public AppDbContext Db { get; }

        public Printer Printer { get; }

        public DateTimeOffset Now { get; }

        public PrinterCalibrationContextService Service { get; }

        public BackendCapabilities Capabilities { get; set; }

        public PrinterStatusSnapshot? Status { get; set; }

        public ResolvedCalibrationProfiles Profiles { get; set; }

        public async Task AddPrinterAsync(string name)
        {
            Printer printer = CreatePrinter(Now.UtcDateTime);
            printer.Name = name;
            _ = Db.Printers.Add(printer);
            _ = await Db.SaveChangesAsync();
        }

        public async Task<CalibrationContextDto> GetContextAsync() =>
            (await Service.GetContextAsync(
                Printer.Id,
                configurationRevision: null,
                capturedBySubject: "test-subject",
                profileAccessScope: ProfileAccess,
                cancellationToken: CancellationToken.None))
            .Value ?? throw new InvalidOperationException("Missing calibration context.");

        public void VerifyNoProfileResolverCalls()
        {
            _profileResolver.Verify(
                resolver => resolver.IsAvailableAsync(It.IsAny<CancellationToken>()),
                Times.Never);
            _profileResolver.Verify(
                resolver => resolver.ResolveAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<Guid>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CalibrationProfileAccessScope>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        public void VerifySingleProfileResolution()
        {
            _profileResolver.Verify(
                resolver => resolver.IsAvailableAsync(It.IsAny<CancellationToken>()),
                Times.Never);
            _profileResolver.Verify(
                resolver => resolver.ResolveAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<Guid>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CalibrationProfileAccessScope>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
            _profileResolver.Verify(
                resolver => resolver.ResolveAsync(
                    Printer.CalibrationMachineProfileId!.Value,
                    Printer.CalibrationProcessProfileId!.Value,
                    Printer.CalibrationFilamentProfileId!.Value,
                    ProfileAccess,
                    It.IsAny<CancellationToken>()),
                Times.Once);
            _profileResolver.VerifyNoOtherCalls();
        }

        public void MakeProfileResolverUnavailable(
            string errorCode = "profile_service_unavailable")
        {
            _ = _profileResolver
                .Setup(resolver => resolver.IsAvailableAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            _ = _profileResolver
                .Setup(resolver => resolver.ResolveAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<Guid>(),
                    It.IsAny<Guid>(),
                    It.IsAny<CalibrationProfileAccessScope>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new CalibrationProfileResolverUnavailableException(
                    "The calibration profile resolver failed.",
                    errorCode));
        }

        public static async Task<CalibrationHarness> CreateAsync()
        {
            DbContextOptions<AppDbContext> options =
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase($"calibration-{Guid.NewGuid()}")
                    .Options;
            AppDbContext db = new(options);
            DateTime nowUtc = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
            Printer printer = CreatePrinter(nowUtc);
            _ = db.Printers.Add(printer);
            _ = await db.SaveChangesAsync();
            return new CalibrationHarness(db, printer);
        }

        public PrinterStatusSnapshot CreateStatus(
            bool isOnline,
            string state,
            DateTime observedAtUtc) =>
            new(
                new PrinterStatusDto(
                    Printer.Id,
                    isOnline,
                    state,
                    HotendTemp: 205,
                    BedTemp: 60,
                    HotendTarget: 210,
                    BedTarget: 60),
                observedAtUtc,
                isOnline ? observedAtUtc : Now.UtcDateTime.AddMinutes(-1),
                "backend");

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();

        private static Printer CreatePrinter(DateTime nowUtc)
        {
            Guid printerId = Guid.NewGuid();
            return new Printer
            {
                Id = printerId,
                Name = "Explicit Klipper printer",
                ServerUrl = "http://10.0.0.42",
                BackendPort = 7125,
                Backend = (int)PrinterBackend.Moonraker,
                ManufacturerId = Guid.NewGuid(),
                ModelId = Guid.NewGuid(),
                IsEnabled = true,
                FirmwareFamily = PrinterFirmwareFamily.Klipper,
                GcodeDialect = PrinterGcodeDialect.Klipper,
                FirmwareDetectionSource = FirmwareDetectionSource.Printer,
                FirmwareVersion = "v0.12.0",
                FirmwareDetectionVersion = "moonraker-printer-info-v1",
                FirmwareDetectionConfidence = 1m,
                FirmwareDetectedAtUtc = nowUtc,
                FirmwareIdentityVerified = true,
                BackendVersion = "v0.9.3",
                BackendApiVersion = "v1",
                MaxBuildVolumeX = 250,
                MaxBuildVolumeY = 250,
                MaxBuildVolumeZ = 250,
                BedOriginX = 0,
                BedOriginY = 0,
                PrintablePolygonJson =
                    """[{"x":0,"y":0},{"x":250,"y":0},{"x":250,"y":250},{"x":0,"y":250}]""",
                ExcludedRegionsJson = "[]",
                CalibrationMotionType = CalibrationMotionType.CoreXY,
                MaxPrintSpeed = 300,
                MaxTravelSpeed = 500,
                MaxAcceleration = 10000,
                MaxTravelAcceleration = 12000,
                CalibrationHasHeatedBed = true,
                MaxBedTemp = 120,
                CalibrationHasEnclosure = false,
                HasHeatedChamber = false,
                ActiveToolheadIndex = 0,
                SupportsPressureAdvance = true,
                SupportsFirmwareRetraction = true,
                CalibrationHardwareVerifiedAtUtc = nowUtc,
                CalibrationSlicerEngine = CalibrationContractConstants.SlicerEngine,
                CalibrationSlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                CalibrationSlicerVersion = CalibrationContractConstants.SlicerVersion,
                CalibrationProfileFormat = CalibrationContractConstants.ProfileFormat,
                CalibrationMachineProfileId = Guid.NewGuid(),
                CalibrationProcessProfileId = Guid.NewGuid(),
                CalibrationFilamentProfileId = Guid.NewGuid(),
                ApiKey = "printer-api-key",
                Username = "printer-user",
                Password = "printer-password",
                Toolheads =
                [
                    new Toolhead
                    {
                        Id = Guid.NewGuid(),
                        PrinterId = printerId,
                        Name = "T0",
                        Index = 0,
                        IsPrimary = true,
                        ToolheadType = ToolheadType.Physical,
                        OffsetX = 0,
                        OffsetY = 0,
                        OffsetZ = 0,
                        NozzleDiameter = 0.4,
                        NozzleType = NozzleType.Brass,
                        NozzleMaterial = "brass",
                        NozzleMaxTemperature = 300,
                        NozzleIsHardened = false,
                        HotendMaxTemperature = 300,
                        MaxVolumetricFlow = 15,
                        DriveType = "direct",
                        IsDirectDrive = true,
                        ExtruderGearRatio = "50:10",
                        SupportedMaterials = ["PLA", "PETG"],
                    },
                ],
            };
        }

        private static ResolvedCalibrationProfiles CreateProfiles(
            Printer printer,
            DateTime updatedAtUtc)
        {
            ResolvedCalibrationProfile machine = new(
                Id: printer.CalibrationMachineProfileId!.Value,
                Kind: "machine",
                Name: "Test Machine",
                SlicerType: CalibrationContractConstants.SlicerEngine,
                SlicerDistribution: CalibrationContractConstants.SlicerDistribution,
                SlicerVersion: CalibrationContractConstants.SlicerVersion,
                ProfileFormat: CalibrationContractConstants.ProfileFormat,
                UpdatedAtUtc: updatedAtUtc,
                RawJson: """{"gcode_flavor":"klipper","nozzle_diameter":[0.4]}""",
                StoredSha256: null,
                PrinterModelId: printer.ModelId,
                SpecificPrinterId: null,
                CompatiblePrinters: null,
                LayerHeight: null,
                InfillPercentage: null,
                PrintSpeed: null,
                NozzleTemperature: null,
                BedTemperature: null,
                MaxVolumetricFlow: null,
                Material: null,
                Manufacturer: "Test",
                Sku: null);
            ResolvedCalibrationProfile process = new(
                Id: printer.CalibrationProcessProfileId!.Value,
                Kind: "process",
                Name: "Standard 0.20",
                SlicerType: CalibrationContractConstants.SlicerEngine,
                SlicerDistribution: CalibrationContractConstants.SlicerDistribution,
                SlicerVersion: CalibrationContractConstants.SlicerVersion,
                ProfileFormat: CalibrationContractConstants.ProfileFormat,
                UpdatedAtUtc: updatedAtUtc,
                RawJson: """{"layer_height":0.2,"infill_density":20}""",
                StoredSha256: null,
                PrinterModelId: printer.ModelId,
                SpecificPrinterId: printer.Id,
                CompatiblePrinters: machine.Name,
                LayerHeight: 0.2,
                InfillPercentage: 20,
                PrintSpeed: 100,
                NozzleTemperature: null,
                BedTemperature: null,
                MaxVolumetricFlow: null,
                Material: null,
                Manufacturer: null,
                Sku: null);
            ResolvedCalibrationProfile filament = new(
                Id: printer.CalibrationFilamentProfileId!.Value,
                Kind: "filament",
                Name: "Test PLA",
                SlicerType: CalibrationContractConstants.SlicerEngine,
                SlicerDistribution: CalibrationContractConstants.SlicerDistribution,
                SlicerVersion: CalibrationContractConstants.SlicerVersion,
                ProfileFormat: CalibrationContractConstants.ProfileFormat,
                UpdatedAtUtc: updatedAtUtc,
                RawJson: """{"filament_max_volumetric_speed":12}""",
                StoredSha256: null,
                PrinterModelId: null,
                SpecificPrinterId: null,
                CompatiblePrinters: machine.Name,
                LayerHeight: null,
                InfillPercentage: null,
                PrintSpeed: 100,
                NozzleTemperature: 210,
                BedTemperature: 60,
                MaxVolumetricFlow: 12,
                Material: "PLA",
                Manufacturer: "Test",
                Sku: "PLA-001");
            return new(machine, process, filament);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
