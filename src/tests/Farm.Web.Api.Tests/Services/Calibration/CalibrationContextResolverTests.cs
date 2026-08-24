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

// Retains the GetContextAsync-focused coverage that used to live in
// PrinterCalibrationContextServiceTests.cs. #1943 removed the fleet-wide eligibility
// projection (GetCandidatesAsync, 77 rejection codes) and its dedicated test file, but
// GetContextAsync itself was relocated -- not deleted -- into CalibrationContextResolver
// because CalibrationProjectService still depends on it for non-eligibility data (build
// volume, toolheads, firmware/slicer identity, snapshot hashing). This file preserves that
// still-live behavior's regression coverage under the new type name.
public sealed class CalibrationContextResolverTests
{
    private static readonly CalibrationProfileAccessScope ProfileAccess =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), BypassOwnership: false);

    [Fact]
    public async Task GetContextAsync_WithExplicitNozzleDiameterOverridingProfile_PrefersOverrideButStillFlagsMismatch()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.Toolheads.Single().NozzleDiameter = 0.6;
        _ = await harness.Db.SaveChangesAsync();
        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine!.WithRawJson(
                """{"gcode_flavor":"klipper","nozzle_diameter":[0.4]}"""),
        };

        CalibrationContextDto context = await harness.GetContextAsync();

        CalibrationToolheadDto activeToolhead =
            context.Toolheads.Should().ContainSingle().Which;
        _ = activeToolhead.NozzleDiameter.Should().Be(0.6);
        _ = context.RejectionReasons.Select(reason => reason.Code).Should().Contain(
            "profile_nozzle_mismatch");
    }

    [Fact]
    public async Task GetContextAsync_WithDerivedToolheadFactsAndMatchingProfile_DoesNotRunCrossValidationAgainstStaleRawColumns()
    {
        // #1614 AC-3 regression: the pre-existing profile cross-checks
        // (ValidateMachineProfile's nozzle-layout comparison and ValidateFilamentSafety's
        // hotend/heated-bed checks) must run against the *effective* (explicit-or-derived)
        // values, not the raw (now-null) Calibration*/Toolhead columns. Otherwise a printer
        // relying entirely on profile derivation for these fields is spuriously rejected by
        // the very profile that supplies them, or has its safety checks silently skipped.
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.CalibrationHasHeatedBed = null;
        Toolhead toolhead = harness.Printer.Toolheads.Single();
        toolhead.NozzleDiameter = null;
        toolhead.HotendMaxTemperature = null;
        _ = await harness.Db.SaveChangesAsync();
        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine!.WithRawJson(
                """
                {
                    "gcode_flavor": "klipper",
                    "nozzle_diameter": [0.4],
                    "max_hotend_temp": [300],
                    "has_heated_bed": true
                }
                """),
        };

        CalibrationContextDto context = await harness.GetContextAsync();

        CalibrationToolheadDto activeToolhead =
            context.Toolheads.Should().ContainSingle().Which;
        _ = activeToolhead.NozzleDiameter.Should().Be(0.4);
        _ = activeToolhead.HotendMaxTemperature.Should().Be(300);
        _ = context.HasHeatedBed.Should().BeTrue();
        _ = context.Eligible.Should().BeTrue(
            string.Join(", ", context.RejectionReasons.Select(reason => reason.Code)));
        _ = context.RejectionReasons.Select(reason => reason.Code).Should().NotContain(
            "profile_nozzle_mismatch",
            "filament_hotend_temperature_exceeds_limit",
            "filament_bed_temperature_requires_heated_bed");
    }

    [Fact]
    public void Printer_HasHeatedChamber_IsNotRenamedToCalibrationHasHeatedChamberChannel()
    {
        // Pins the rollback of issue #1617's rename (issue #1947): HasHeatedChamber backs a
        // general dispatch-safety property, not a calibration-only channel, so it must not be
        // Calibration*-prefixed.
        _ = typeof(Printer).GetProperty(nameof(Printer.HasHeatedChamber))
            .Should().NotBeNull("the unprefixed HasHeatedChamber property must exist on Printer");
        _ = typeof(Printer).GetProperty("CalibrationHasHeatedChamber")
            .Should().BeNull("the renamed calibration channel must not exist on Printer");
    }

    [Fact]
    public void Printer_HasHeatedChamber_IsNullableBool()
    {
        System.Reflection.PropertyInfo? property =
            typeof(Printer).GetProperty(nameof(Printer.HasHeatedChamber));
        _ = property.Should().NotBeNull();
        _ = property!.PropertyType.Should().Be(typeof(bool?));
    }

    [Fact]
    public async Task GetContextAsync_WithProfileMismatches_ReturnsTypedReasons()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine!.WithRawJson(
                """{"gcode_flavor":"klipper","nozzle_diameter":[0.6]}""") with
            {
                StoredSha256 = new string('0', 64),
            },
            Filament = harness.Profiles.Filament! with
            {
                NozzleTemperature = 400,
            },
        };

        CalibrationContextDto candidate = await harness.GetContextAsync();

        _ = candidate.ProfilesEvaluated.Should().BeTrue();
        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Select(reason => reason.Code).Should().Contain(
            "profile_hash_mismatch",
            "profile_nozzle_mismatch",
            "filament_hotend_temperature_exceeds_limit");
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

    [Fact]
    public async Task GetContextAsync_WithoutRegisteredProfileResolver_ReturnsStableServiceError()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        CalibrationContextResolver service = harness.CreateService(profileResolver: null);

        CalibrationServiceResult<CalibrationContextDto> result =
            await service.GetContextAsync(
                harness.Printer.Id,
                configurationRevision: null,
                capturedBySubject: "test-subject",
                profileAccessScope: ProfileAccess,
                cancellationToken: CancellationToken.None);

        _ = result.Value.Should().BeNull();
        _ = result.ErrorCode.Should().Be("profile_service_unavailable");
        harness.VerifyNoProfileResolverCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetContextAsync_WithoutResolverAndMissingOrEmptyProfileIds_ReturnsAuthoritativeTypedReasons(
        bool useEmptyIds)
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        Guid? profileId = useEmptyIds ? Guid.Empty : null;
        harness.Printer.CalibrationMachineProfileId = profileId;
        harness.Printer.CalibrationProcessProfileId = profileId;
        harness.Printer.CalibrationFilamentProfileId = profileId;
        _ = await harness.Db.SaveChangesAsync();
        CalibrationContextResolver service = harness.CreateService(profileResolver: null);

        CalibrationServiceResult<CalibrationContextDto> result =
            await service.GetContextAsync(
                harness.Printer.Id,
                configurationRevision: null,
                capturedBySubject: "test-subject",
                profileAccessScope: ProfileAccess,
                cancellationToken: CancellationToken.None);

        _ = result.ErrorCode.Should().BeNull();
        _ = result.Value.Should().NotBeNull();
        CalibrationContextDto context = result.Value!;
        _ = context.ProfilesEvaluated.Should().BeTrue();
        _ = context.Eligible.Should().BeFalse();
        _ = context.RejectionReasons.Select(reason => reason.Code).Should().Contain(
            "machine_profile_missing",
            "process_profile_missing",
            "filament_profile_missing");
        harness.VerifyNoProfileResolverCalls();
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
    public async Task GetContextAsync_WithRequiredNozzleHrcAndNotHardenedNozzle_ReturnsTypedReason()
    {
        // #1827 dispatch/backward-compat parity: prior to this test, the
        // "profile_nozzle_material_mismatch" rejection (CalibrationContextResolver.cs,
        // ValidateFilamentSafety's required_nozzle_HRC check) had zero regression coverage.
        // Toolhead.NozzleIsHardened is a separately-persisted, independently-set fact -- it is
        // NOT derived from the #1824 NozzleMaterial catalog -- so this test also locks in that
        // this consumer's behavior is unaffected by the catalog migration.
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.Toolheads.Single().NozzleIsHardened = false;
        _ = await harness.Db.SaveChangesAsync();
        harness.Profiles = harness.Profiles with
        {
            Filament = harness.Profiles.Filament! with
            {
                RawJson = """{"required_nozzle_HRC": 3}""",
            },
        };

        CalibrationContextDto candidate = await harness.GetContextAsync();

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Select(reason => reason.Code)
            .Should().Contain("profile_nozzle_material_mismatch");
    }

    [Fact]
    public async Task GetContextAsync_WithRequiredNozzleHrcAndHardenedNozzle_DoesNotFlagMismatch()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.Toolheads.Single().NozzleIsHardened = true;
        _ = await harness.Db.SaveChangesAsync();
        harness.Profiles = harness.Profiles with
        {
            Filament = harness.Profiles.Filament! with
            {
                RawJson = """{"required_nozzle_HRC": 3}""",
            },
        };

        CalibrationContextDto candidate = await harness.GetContextAsync();

        _ = candidate.RejectionReasons.Select(reason => reason.Code)
            .Should().NotContain("profile_nozzle_material_mismatch");
    }

    [Fact]
    public async Task GetContextAsync_WithoutRequiredNozzleHrc_DoesNotFlagMismatchRegardlessOfHardening()
    {
        // A filament profile with no required_nozzle_HRC field must never trigger the mismatch
        // rejection, even for a non-hardened nozzle -- this is the "existing/unchanged data"
        // backward-compat baseline #1827 asks to confirm.
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.Toolheads.Single().NozzleIsHardened = false;
        _ = await harness.Db.SaveChangesAsync();

        CalibrationContextDto candidate = await harness.GetContextAsync();

        _ = candidate.RejectionReasons.Select(reason => reason.Code)
            .Should().NotContain("profile_nozzle_material_mismatch");
    }

    [Fact]
    public async Task GetContextAsync_WithCredentialBearingProfile_RedactsProfileAndReturnsTypedReason()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine!.WithRawJson(
                """{"gcode_flavor":"klipper","nozzle_diameter":[0.4],"api_key":"secret-value"}"""),
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
            Machine = harness.Profiles.Machine!.WithRawJson(rawJson),
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
            Machine = harness.Profiles.Machine!.WithRawJson(
                """{"nozzle_diameter":[0.4],"gcode_flavor":"klipper"}"""),
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
        // Shared across every printer/model seeded by this harness so
        // GetUnknownModelIdAsync's (ManufacturerId, Name) lookup -- matching
        // EfCatalogRepository.GetUnknownModelIdAsync's production identity, not a bare Name
        // match, per #1922 review -- resolves the same "Unknown Model" row tests already rely
        // on as the default, un-cataloged placeholder.
        private static readonly Guid UnknownManufacturerId = Guid.NewGuid();

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
            Configuration = configuration;
            Service = CreateService(_profileResolver.Object);
        }

        public AppDbContext Db { get; }

        public Printer Printer { get; }

        public DateTimeOffset Now { get; }

        public CalibrationContextResolver Service { get; }

        private IConfiguration Configuration { get; }

        public BackendCapabilities Capabilities { get; set; }

        public PrinterStatusSnapshot? Status { get; set; }

        public ResolvedCalibrationProfiles Profiles { get; set; }

        public CalibrationContextResolver CreateService(
            ICalibrationProfileResolver? profileResolver) =>
            new(
                Db,
                _statusReader.Object,
                _capabilityFactory.Object,
                Configuration,
                new FixedTimeProvider(Now),
                profileResolver);

        public async Task AddPrinterAsync(string name)
        {
            Printer printer = CreatePrinter(Now.UtcDateTime);
            printer.Name = name;
            _ = Db.Printers.Add(printer);
            _ = Db.PrinterModels.Add(new PrinterModel
            {
                Id = printer.ModelId,
                Name = "Unknown Model",
                ManufacturerId = UnknownManufacturerId,
            });
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

            // Mirrors the real-world "Unknown" manufacturer + "Unknown Model" sentinel
            // (EfCatalogRepository.GetUnknownModelIdAsync) that every printer's non-nullable
            // ModelId resolves to by default. Model is a required FK
            // (PrinterConfiguration.HasOne(p => p.Model)), so CalibrationContextResolver's
            // Model.Toolheads Include chain (#1922) performs an inner join and would otherwise
            // silently drop every printer whose ModelId does not resolve.
            _ = db.Manufacturers.Add(new Manufacturer
            {
                Id = UnknownManufacturerId,
                Name = "Unknown",
            });
            _ = db.PrinterModels.Add(new PrinterModel
            {
                Id = printer.ModelId,
                Name = "Unknown Model",
                ManufacturerId = UnknownManufacturerId,
            });
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
            ResolvedCalibrationProfile machine = new ResolvedCalibrationProfile(
                Id: printer.CalibrationMachineProfileId!.Value,
                Kind: "machine",
                Name: "Test Machine",
                SlicerType: CalibrationContractConstants.SlicerEngine,
                SlicerDistribution: CalibrationContractConstants.SlicerDistribution,
                SlicerVersion: CalibrationContractConstants.SlicerVersion,
                ProfileFormat: CalibrationContractConstants.ProfileFormat,
                UpdatedAtUtc: updatedAtUtc,
                RawJson: null,
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
                Sku: null)
                .WithRawJson("""{"gcode_flavor":"klipper","nozzle_diameter":[0.4]}""");
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
