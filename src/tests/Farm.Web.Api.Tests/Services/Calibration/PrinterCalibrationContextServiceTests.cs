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
        _ = candidate.ProfilesEvaluated.Should().BeFalse();
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
    public async Task GetCandidatesAsync_WithUnsetGcodeDialectAndKlipperFirmwareFamily_DerivesKlipperDialectFromFirmware()
    {
        // #1614 AC-2/§4.5.1 regression: firmware.gcodeDialect is sourced from firmware
        // detection only, never from the resolved machine profile. When the explicit
        // GcodeDialect column is unset but the detected FirmwareFamily is Klipper, the
        // effective dialect must fall back to Klipper rather than blocking eligibility.
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.GcodeDialect = PrinterGcodeDialect.Unknown;
        _ = await harness.Db.SaveChangesAsync();

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeTrue(
            string.Join(", ", candidate.RejectionReasons.Select(reason => reason.Code)));
        _ = candidate.Firmware.GcodeDialect.Should().Be("Klipper");
        _ = candidate.RejectionReasons.Select(reason => reason.Code).Should().NotContain(
            "gcode_dialect_unknown");
    }

    [Fact]
    public async Task GetCandidatesAsync_WithUnsetGcodeDialectAndNonKlipperFirmwareFamily_LeavesDialectUnknown()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.GcodeDialect = PrinterGcodeDialect.Unknown;
        harness.Printer.FirmwareFamily = PrinterFirmwareFamily.Other;
        _ = await harness.Db.SaveChangesAsync();

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.Firmware.GcodeDialect.Should().Be("Unknown");
        _ = candidate.RejectionReasons.Select(reason => reason.Code).Should().Contain(
            "gcode_dialect_unknown");
    }

    [Fact]
    public async Task GetCandidatesAsync_WithMultiplePhysicalToolheads_OnlyActiveToolheadDerivesFromMachineProfile()
    {
        // #1613 §4.6 regression: the machine profile describes only the currently-active
        // tool, so non-active physical toolheads must never be coalesced with
        // profile-derived nozzle facts, even when their own explicit values are null.
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.ActiveToolheadIndex = 0;
        Toolhead active = harness.Printer.Toolheads.Single();
        active.NozzleDiameter = null;
        active.NozzleType = null;
        active.NozzleMaxTemperature = null;
        active.HotendMaxTemperature = null;
        Toolhead inactive = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = harness.Printer.Id,
            Name = "T1",
            Index = 1,
            ToolheadType = ToolheadType.Physical,
            NozzleDiameter = null,
            NozzleType = null,
            NozzleMaterial = "brass",
            NozzleMaxTemperature = null,
            NozzleIsHardened = false,
            HotendMaxTemperature = null,
            SupportedMaterials = ["PLA"],
        };
        _ = harness.Db.Toolheads.Add(inactive);
        _ = await harness.Db.SaveChangesAsync();
        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine!.WithRawJson(
                """
                {
                    "gcode_flavor": "klipper",
                    "nozzle_diameter": [0.4],
                    "nozzle_type": "brass",
                    "max_hotend_temp": [300]
                }
                """),
        };

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        CalibrationToolheadDto activeDto =
            candidate.Toolheads.Should().ContainSingle(t => t.Index == 0).Which;
        _ = activeDto.NozzleDiameter.Should().Be(0.4);
        _ = activeDto.NozzleType.Should().Be("Brass");
        _ = activeDto.HotendMaxTemperature.Should().Be(300);
        _ = candidate.RejectionReasons.Select(reason => reason.Field).Should().Contain(
            field => field == "toolheads[1].nozzleDiameter");
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

    /// <summary>
    /// A machine profile RawJson that fully supplies every AC-2-derivable fact (#1614): printable
    /// area/height, motion type, acceleration/feedrate, heated-bed/chamber flags, and active
    /// toolhead nozzle facts.
    /// </summary>
    private const string FullyDerivableMachineProfileJson =
        """
        {
            "printable_area": ["0x0", "250x0", "250x250", "0x250"],
            "printable_height": 250,
            "machine_max_acceleration_x": [10000],
            "machine_max_speed_x": [500],
            "has_heated_bed": true,
            "has_heated_chamber": false,
            "nozzle_diameter": [0.4],
            "nozzle_type": "brass",
            "max_hotend_temp": [300],
            "printer_type": "corexy"
        }
        """;

    [Fact]
    public async Task GetCandidatesAsync_WithDerivableFieldsNullAndMachineProfileSupplyingThem_ReturnsEligibleCandidate()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.MaxBuildVolumeX = null;
        harness.Printer.MaxBuildVolumeY = null;
        harness.Printer.MaxBuildVolumeZ = null;
        harness.Printer.BedOriginX = null;
        harness.Printer.BedOriginY = null;
        harness.Printer.PrintablePolygonJson = null;
        harness.Printer.CalibrationMotionType = null;
        harness.Printer.MaxAcceleration = null;
        harness.Printer.MaxTravelSpeed = null;
        harness.Printer.CalibrationHasHeatedBed = null;
        harness.Printer.CalibrationHasHeatedChamber = null;
        harness.Printer.CalibrationSlicerEngine = null;
        harness.Printer.CalibrationSlicerDistribution = null;
        harness.Printer.CalibrationSlicerVersion = null;
        harness.Printer.CalibrationProfileFormat = null;
        Toolhead toolhead = harness.Printer.Toolheads.Single();
        toolhead.NozzleDiameter = null;
        toolhead.NozzleType = null;
        toolhead.NozzleMaxTemperature = null;
        toolhead.HotendMaxTemperature = null;
        _ = await harness.Db.SaveChangesAsync();
        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine!.WithRawJson(FullyDerivableMachineProfileJson),
        };

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeTrue();
        _ = candidate.RejectionReasons.Should().BeEmpty();
        _ = candidate.MissingInputs.Should().BeEmpty();
        _ = candidate.BuildVolume.X.Should().Be(250);
        _ = candidate.BuildVolume.Y.Should().Be(250);
        _ = candidate.BedOrigin.X.Should().Be(0);
        _ = candidate.BedOrigin.Y.Should().Be(0);
        _ = candidate.MotionType.Should().Be("CoreXY");
        _ = candidate.MaxAcceleration.Should().Be(10000);
        _ = candidate.MaxTravelSpeed.Should().Be(500);
        _ = candidate.HasHeatedBed.Should().BeTrue();
        _ = candidate.HasHeatedChamber.Should().BeFalse();
        CalibrationToolheadDto activeToolhead = candidate.Toolheads.Should().ContainSingle().Which;
        _ = activeToolhead.NozzleDiameter.Should().Be(0.4);
        _ = activeToolhead.NozzleType.Should().Be("Brass");
        _ = activeToolhead.NozzleMaxTemperature.Should().Be(300);
        _ = activeToolhead.HotendMaxTemperature.Should().Be(300);
        harness.VerifySingleProfileResolution();
    }

    [Fact]
    public async Task GetCandidatesAsync_WithPrinterAndProfileSilent_FallsBackToCatalogModel()
    {
        // #1922: when the printer row and machine profile are both silent, the printer's
        // catalog model (PrinterModel/PrinterModelToolhead/component definitions) is the
        // third fallback tier. Fully populating the catalog model must let an otherwise-blank
        // printer reach eligibility with zero missing inputs and zero manual entry.
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.MaxBuildVolumeX = null;
        harness.Printer.MaxBuildVolumeY = null;
        harness.Printer.MaxBuildVolumeZ = null;
        harness.Printer.BedOriginX = null;
        harness.Printer.BedOriginY = null;
        harness.Printer.PrintablePolygonJson = null;
        harness.Printer.CalibrationMotionType = null;
        harness.Printer.MaxAcceleration = null;
        harness.Printer.MaxTravelAcceleration = null;
        harness.Printer.CalibrationHasHeatedBed = null;
        harness.Printer.CalibrationHasHeatedChamber = null;
        harness.Printer.CalibrationHasEnclosure = null;
        harness.Printer.ActiveToolheadIndex = null;
        Toolhead toolhead = harness.Printer.Toolheads.Single();
        toolhead.NozzleDiameter = null;
        toolhead.NozzleType = null;
        toolhead.NozzleMaterial = null;
        toolhead.NozzleMaxTemperature = null;
        toolhead.NozzleIsHardened = null;
        toolhead.HotendMaxTemperature = null;
        toolhead.MaxVolumetricFlow = null;
        toolhead.DriveType = null;
        toolhead.IsDirectDrive = null;
        toolhead.ExtruderGearRatio = null;
        toolhead.SupportedMaterials = null;
        toolhead.OffsetX = null;
        toolhead.OffsetY = null;
        toolhead.OffsetZ = null;
        _ = await harness.Db.SaveChangesAsync();
        harness.Profiles = harness.Profiles with
        {
            // Keep the machine profile itself silent on nozzle diameter so this test genuinely
            // exercises "printer row AND machine profile both silent" for every catalog-coverable
            // field (#1922), rather than exercising machine-profile-derived precedence.
            Machine = harness.Profiles.Machine!.WithRawJson("""{"gcode_flavor":"klipper"}"""),
        };
        await CalibrationHarness.SeedFullyPopulatedCatalogModelAsync(
            harness.Db, harness.Printer.ModelId, toolhead.Index);

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeTrue(
            string.Join(", ", candidate.RejectionReasons.Select(reason => reason.Code)));
        _ = candidate.MissingInputs.Should().BeEmpty();
        _ = candidate.BuildVolume.X.Should().Be(300);
        _ = candidate.BuildVolume.Y.Should().Be(300);
        _ = candidate.BuildVolume.Z.Should().Be(300);
        _ = candidate.BedOrigin.X.Should().Be(0);
        _ = candidate.BedOrigin.Y.Should().Be(0);
        _ = candidate.PrintablePolygon.Should().NotBeNull();
        _ = candidate.MotionType.Should().Be("Cartesian");
        _ = candidate.MaxAcceleration.Should().Be(5000);
        _ = candidate.MaxTravelAcceleration.Should().Be(6000);
        _ = candidate.HasHeatedBed.Should().BeTrue();
        _ = candidate.HasHeatedChamber.Should().BeFalse();
        _ = candidate.HasEnclosure.Should().BeTrue();
        _ = candidate.ActiveToolheadIndex.Should().Be(0);
        CalibrationToolheadDto activeToolhead = candidate.Toolheads.Should().ContainSingle().Which;
        _ = activeToolhead.NozzleDiameter.Should().Be(0.6);
        _ = activeToolhead.NozzleType.Should().Be("HardenedSteel");
        _ = activeToolhead.NozzleMaterial.Should().Be("HardenedSteel");
        _ = activeToolhead.NozzleMaxTemperature.Should().Be(500);
        _ = activeToolhead.NozzleIsHardened.Should().BeTrue();
        _ = activeToolhead.HotendMaxTemperature.Should().Be(400);
        _ = activeToolhead.MaxVolumetricFlow.Should().Be(20);
        _ = activeToolhead.DriveType.Should().Be("bowden");
        _ = activeToolhead.IsDirectDrive.Should().BeFalse();
        _ = activeToolhead.ExtruderGearRatio.Should().Be("3:1");
        _ = activeToolhead.SupportedMaterials.Should().BeEquivalentTo(["ABS"]);
        _ = activeToolhead.Offset.X.Should().Be(0);
        _ = activeToolhead.Offset.Y.Should().Be(0);
        _ = activeToolhead.Offset.Z.Should().Be(0);
    }

    [Fact]
    public async Task GetCandidatesAsync_WithExplicitPrinterRowValues_OverridesCatalogModel()
    {
        // #1922 AC: an explicit printer-row value always overrides the catalog default, even
        // when the catalog model asserts a conflicting value for the same fact.
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        await CalibrationHarness.SeedFullyPopulatedCatalogModelAsync(
            harness.Db, harness.Printer.ModelId, harness.Printer.Toolheads.Single().Index);

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeTrue(
            string.Join(", ", candidate.RejectionReasons.Select(reason => reason.Code)));
        _ = candidate.BuildVolume.X.Should().Be(250);
        _ = candidate.MotionType.Should().Be("CoreXY");
        _ = candidate.MaxAcceleration.Should().Be(10000);
        _ = candidate.MaxTravelAcceleration.Should().Be(12000);
        _ = candidate.HasEnclosure.Should().BeFalse();
        CalibrationToolheadDto activeToolhead = candidate.Toolheads.Should().ContainSingle().Which;
        _ = activeToolhead.NozzleDiameter.Should().Be(0.4);
        _ = activeToolhead.NozzleType.Should().Be("Brass");
        _ = activeToolhead.HotendMaxTemperature.Should().Be(300);
        _ = activeToolhead.DriveType.Should().Be("direct");
    }

    [Fact]
    public async Task GetCandidatesAsync_WithUnsetActiveToolheadIndex_ResolvesFromCatalogPrimaryToolhead()
    {
        // #1922 AC: activeToolheadIndex resolves from PrinterModelToolhead.IsPrimary when the
        // printer row does not assert one explicitly.
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.ActiveToolheadIndex = null;
        Toolhead toolhead = harness.Printer.Toolheads.Single();
        toolhead.Index = 2;
        _ = await harness.Db.SaveChangesAsync();
        await CalibrationHarness.SeedFullyPopulatedCatalogModelAsync(
            harness.Db, harness.Printer.ModelId, primaryToolheadIndex: 2);

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.ActiveToolheadIndex.Should().Be(2);
        _ = candidate.Eligible.Should().BeTrue(
            string.Join(", ", candidate.RejectionReasons.Select(reason => reason.Code)));
    }

    [Fact]
    public async Task GetCandidatesAsync_WithSingleToolheadAndUnsetOffsets_DoesNotRequireManualOffsetEntry()
    {
        // #1922 AC: single-toolhead printers do not require manual toolhead offsets.
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        Toolhead toolhead = harness.Printer.Toolheads.Single();
        toolhead.OffsetX = null;
        toolhead.OffsetY = null;
        toolhead.OffsetZ = null;
        _ = await harness.Db.SaveChangesAsync();

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeTrue(
            string.Join(", ", candidate.RejectionReasons.Select(reason => reason.Code)));
        _ = candidate.MissingInputs.Should().NotContain(
            "toolheads[0].offset.x", "toolheads[0].offset.y", "toolheads[0].offset.z");
        CalibrationToolheadDto activeToolhead = candidate.Toolheads.Should().ContainSingle().Which;
        _ = activeToolhead.Offset.X.Should().Be(0);
        _ = activeToolhead.Offset.Y.Should().Be(0);
        _ = activeToolhead.Offset.Z.Should().Be(0);
    }

    [Fact]
    public async Task GetCandidatesAsync_WithCatalogFallbackAndOnlyMaxPrintSpeedMissing_ReportsOnlyGenuinelyMissingInput()
    {
        // #1922 AC: missingInputs reports only what is genuinely underivable. maxPrintSpeed has
        // no catalog (or machine-profile) fallback tier, so it must remain the sole reported gap
        // while every catalog-coverable field resolves silently.
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.MaxBuildVolumeX = null;
        harness.Printer.MaxBuildVolumeY = null;
        harness.Printer.MaxBuildVolumeZ = null;
        harness.Printer.BedOriginX = null;
        harness.Printer.BedOriginY = null;
        harness.Printer.PrintablePolygonJson = null;
        harness.Printer.CalibrationMotionType = null;
        harness.Printer.MaxAcceleration = null;
        harness.Printer.MaxTravelAcceleration = null;
        harness.Printer.CalibrationHasHeatedBed = null;
        harness.Printer.CalibrationHasHeatedChamber = null;
        harness.Printer.CalibrationHasEnclosure = null;
        harness.Printer.MaxPrintSpeed = null;
        _ = await harness.Db.SaveChangesAsync();
        await CalibrationHarness.SeedFullyPopulatedCatalogModelAsync(
            harness.Db, harness.Printer.ModelId, harness.Printer.Toolheads.Single().Index);

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.MissingInputs.Should().Contain("maxPrintSpeed");
        _ = candidate.MissingInputs.Should().NotContain(
            "buildVolume.x", "buildVolume.y", "buildVolume.z",
            "motionType", "maxAcceleration", "maxTravelAcceleration",
            "hasHeatedBed", "hasHeatedChamber", "hasEnclosure");
        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Select(reason => reason.Code).Should().ContainSingle(
            code => code == "max_print_speed_missing");
    }

    [Fact]
    public async Task GetCandidatesAsync_WithGenericUnknownModelSeedRow_DoesNotLeakItsPlaceholderBoolDefaults()
    {
        // Regression for #1922: the real production "Unknown Model" catalog seed row
        // (src/api/Data/seed/printer-models.yaml) is NOT a blank/empty PrinterModel -- it
        // already asserts a 200x200x200 build volume, a "Stock Toolhead", and
        // HasHeatedBed=true/HasEnclosure=false/HasHeatedChamber=false so it renders sensibly
        // as a generic placeholder elsewhere in the product. None of that was curated for this
        // specific printer, so it must never be surfaced as a derived calibration fact. Model
        // this exact shape here (rather than a truly-empty PrinterModel) to prove the sentinel
        // is identified by name, not by "any field happens to be unset".
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.MaxBuildVolumeX = null;
        harness.Printer.MaxBuildVolumeY = null;
        harness.Printer.MaxBuildVolumeZ = null;
        harness.Printer.BedOriginX = null;
        harness.Printer.BedOriginY = null;
        harness.Printer.PrintablePolygonJson = null;
        harness.Printer.CalibrationMotionType = null;
        harness.Printer.CalibrationHasHeatedBed = null;
        harness.Printer.CalibrationHasHeatedChamber = null;
        harness.Printer.CalibrationHasEnclosure = null;
        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine!.WithRawJson("""{"gcode_flavor":"klipper"}"""),
        };
        PrinterModel unknownModel = await harness.Db.PrinterModels.SingleAsync(
            model => model.Id == harness.Printer.ModelId);
        unknownModel.Name.Should().Be("Unknown Model");
        unknownModel.MotionType = (int)CalibrationMotionType.Cartesian;
        unknownModel.MaxX = 200;
        unknownModel.MaxY = 200;
        unknownModel.MaxZ = 200;
        unknownModel.HasHeatedBed = true;
        unknownModel.HasEnclosure = false;
        unknownModel.HasHeatedChamber = false;
        _ = harness.Db.PrinterModelToolheads.Add(new PrinterModelToolhead
        {
            Id = Guid.NewGuid(),
            PrinterModelId = unknownModel.Id,
            Name = "Stock Toolhead",
            Index = harness.Printer.Toolheads.Single().Index,
            IsPrimary = true,
        });
        _ = await harness.Db.SaveChangesAsync();

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.MissingInputs.Should().Contain(
            "buildVolume.x", "buildVolume.y", "buildVolume.z",
            "motionType", "hasHeatedBed", "hasHeatedChamber", "hasEnclosure");
    }

    [Fact]
    public async Task GetCandidatesAsync_WithCuratedModelSharingUnknownModelNameUnderDifferentManufacturer_StillDerivesItsFacts()
    {
        // Regression for #1922 (round 2 review): PrinterModel only enforces uniqueness on
        // (ManufacturerId, Name), not Name alone, so a curated model that merely happens to be
        // named "Unknown Model" under a manufacturer OTHER than the reserved "Unknown" sentinel
        // manufacturer must NOT be discarded -- only the (Unknown manufacturer, "Unknown Model")
        // identity is the sentinel. Model the exact identity collision here to prove the fix
        // matches by ManufacturerId + Name, not by Name in isolation.
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.MaxBuildVolumeX = null;
        harness.Printer.MaxBuildVolumeY = null;
        harness.Printer.MaxBuildVolumeZ = null;
        harness.Printer.BedOriginX = null;
        harness.Printer.BedOriginY = null;
        harness.Printer.PrintablePolygonJson = null;
        harness.Printer.CalibrationMotionType = null;
        harness.Printer.CalibrationHasHeatedBed = null;
        harness.Printer.CalibrationHasHeatedChamber = null;
        harness.Printer.CalibrationHasEnclosure = null;
        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine!.WithRawJson("""{"gcode_flavor":"klipper"}"""),
        };
        Manufacturer curatedManufacturer = new() { Id = Guid.NewGuid(), Name = "Acme Printers" };
        _ = harness.Db.Manufacturers.Add(curatedManufacturer);
        PrinterModel curatedModel = await harness.Db.PrinterModels.SingleAsync(
            model => model.Id == harness.Printer.ModelId);
        curatedModel.ManufacturerId = curatedManufacturer.Id;
        curatedModel.Name = "Unknown Model";
        curatedModel.MotionType = (int)CalibrationMotionType.Cartesian;
        curatedModel.MaxX = 250;
        curatedModel.MaxY = 250;
        curatedModel.MaxZ = 250;
        curatedModel.HasHeatedBed = true;
        curatedModel.HasEnclosure = false;
        curatedModel.HasHeatedChamber = false;
        _ = harness.Db.PrinterModelToolheads.Add(new PrinterModelToolhead
        {
            Id = Guid.NewGuid(),
            PrinterModelId = curatedModel.Id,
            Name = "Curated Toolhead",
            Index = harness.Printer.Toolheads.Single().Index,
            IsPrimary = true,
        });
        _ = await harness.Db.SaveChangesAsync();

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.MissingInputs.Should().NotContain(
            "buildVolume.x", "buildVolume.y", "buildVolume.z",
            "motionType", "hasHeatedBed", "hasHeatedChamber", "hasEnclosure");
    }

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
    public async Task GetCandidatesAsync_WithNullCalibrationHasHeatedBedAndProfileSilentOnHeatedBed_DoesNotFallBackToGeneralColumn()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.HasHeatedBed = true;
        harness.Printer.CalibrationHasHeatedBed = null;
        _ = await harness.Db.SaveChangesAsync();
        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine!.WithRawJson(
                """{"gcode_flavor":"klipper","nozzle_diameter":[0.4]}"""),
        };

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.HasHeatedBed.Should().BeNull();
        _ = candidate.RejectionReasons.Select(reason => reason.Code).Should().Contain(
            "heated_bed_state_missing");
        _ = candidate.MissingInputs.Should().Contain("hasHeatedBed");
    }

    [Fact]
    public async Task GetCandidatesAsync_WithHasHeatedChamberDerivedFromProfile_UsesHeatedChamberNamingConsistently()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.CalibrationHasHeatedChamber = null;
        harness.Printer.MaxChamberTemp = null;
        _ = await harness.Db.SaveChangesAsync();
        harness.Profiles = harness.Profiles with
        {
            Machine = harness.Profiles.Machine!.WithRawJson(
                """{"gcode_flavor":"klipper","nozzle_diameter":[0.4],"has_heated_chamber":true}"""),
        };

        CalibrationCandidateDto candidate =
            (await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None))
            .Value.Should().ContainSingle().Which;

        _ = candidate.HasHeatedChamber.Should().BeTrue();
        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Select(reason => reason.Code).Should().Contain(
            "max_chamber_temperature_missing");
        _ = candidate.MissingInputs.Should().Contain("maxChamberTemperature");
    }

    [Fact]
    public void Printer_HasHeatedChamber_IsRenamedToCalibrationHasHeatedChamberChannel()
    {
        // Pins issue #1617's rename: Printer must expose the Calibration*-prefixed channel
        // for the heated-chamber fact and must not resurrect the old unprefixed name.
        _ = typeof(Printer).GetProperty(nameof(Printer.CalibrationHasHeatedChamber))
            .Should().NotBeNull("the renamed calibration channel must exist on Printer");
        _ = typeof(Printer).GetProperty("HasHeatedChamber")
            .Should().BeNull("the unprefixed HasHeatedChamber property must not exist on Printer");
    }

    [Fact]
    public void Printer_CalibrationHasHeatedChamber_IsNullableBool()
    {
        System.Reflection.PropertyInfo? property =
            typeof(Printer).GetProperty(nameof(Printer.CalibrationHasHeatedChamber));
        _ = property.Should().NotBeNull();
        _ = property!.PropertyType.Should().Be(typeof(bool?));
    }

    [Fact]
    public async Task GetCandidatesAsync_WithResolverUnavailableDuringDerivation_ReturnsTypedRejectionWithoutThrowing()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        harness.Printer.CalibrationMotionType = null;
        _ = await harness.Db.SaveChangesAsync();
        harness.MakeProfileResolverUnavailable();

        CalibrationServiceResult<IReadOnlyList<CalibrationCandidateDto>> result =
            await harness.Service.GetCandidatesAsync(ProfileAccess, CancellationToken.None);

        _ = result.ErrorCode.Should().BeNull();
        CalibrationCandidateDto candidate = result.Value.Should().ContainSingle().Which;
        _ = candidate.Eligible.Should().BeFalse();
        _ = candidate.RejectionReasons.Should().ContainSingle(reason =>
            reason.Code == "profile_service_unavailable" &&
            reason.Field == "machineProfile");
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

    [Fact]
    public async Task GetContextAsync_WithoutRegisteredProfileResolver_ReturnsStableServiceError()
    {
        await using CalibrationHarness harness = await CalibrationHarness.CreateAsync();
        PrinterCalibrationContextService service = harness.CreateService(profileResolver: null);

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
        PrinterCalibrationContextService service = harness.CreateService(profileResolver: null);

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
        // "profile_nozzle_material_mismatch" rejection (PrinterCalibrationContextService.cs,
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

        public PrinterCalibrationContextService Service { get; }

        private IConfiguration Configuration { get; }

        public BackendCapabilities Capabilities { get; set; }

        public PrinterStatusSnapshot? Status { get; set; }

        public ResolvedCalibrationProfiles Profiles { get; set; }

        public PrinterCalibrationContextService CreateService(
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
            // (PrinterConfiguration.HasOne(p => p.Model)), so PrinterCalibrationContextService's
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

        /// <summary>
        /// Seeds a fully-populated catalog model (<see cref="PrinterModel"/> +
        /// <see cref="PrinterModelToolhead"/> + component definitions) for use as the third
        /// (#1922) calibration fallback tier. All catalog values are deliberately distinct from
        /// <see cref="CreatePrinter"/>'s printer-row defaults so tests can prove which tier a
        /// resolved value actually came from.
        /// </summary>
        public static async Task SeedFullyPopulatedCatalogModelAsync(
            AppDbContext db,
            Guid modelId,
            int primaryToolheadIndex)
        {
            NozzleMaterial nozzleMaterial = new()
            {
                Id = Guid.NewGuid(),
                Name = "HardenedSteel",
                IsHardened = true,
                DefaultMaxTemp = 500,
                IsBuiltIn = true,
            };
            NozzleModelDefinition nozzle = new()
            {
                Id = Guid.NewGuid(),
                Name = "Catalog nozzle",
                ManufacturerId = Guid.NewGuid(),
                Diameter = 0.6,
                MaxTemp = 500,
                NozzleMaterialId = nozzleMaterial.Id,
                NozzleMaterial = nozzleMaterial,
            };
            HotendModelDefinition hotend = new()
            {
                Id = Guid.NewGuid(),
                Name = "Catalog hotend",
                ManufacturerId = Guid.NewGuid(),
                MaxTemp = 400,
                MaxFlowRate = 20,
            };
            ExtruderModelDefinition extruder = new()
            {
                Id = Guid.NewGuid(),
                Name = "Catalog extruder",
                ManufacturerId = Guid.NewGuid(),
                GearRatio = "3:1",
                IsDirectDrive = false,
            };
            PrinterModel? model = await db.PrinterModels.FindAsync(modelId);
            if (model is null)
            {
                model = new PrinterModel { Id = modelId, ManufacturerId = Guid.NewGuid() };
                _ = db.PrinterModels.Add(model);
            }

            model.Name = "Catalog model";
            model.MotionType = (int)CalibrationMotionType.Cartesian;
            model.MaxX = 300;
            model.MaxY = 300;
            model.MaxZ = 300;
            model.HasHeatedBed = true;
            model.HasEnclosure = true;
            model.HasHeatedChamber = false;
            model.MaxAcceleration = 5000;
            model.MaxTravelAcceleration = 6000;
            PrinterModelToolhead toolhead = new()
            {
                Id = Guid.NewGuid(),
                PrinterModelId = modelId,
                Name = "Catalog toolhead",
                Index = primaryToolheadIndex,
                IsPrimary = true,
                NozzleModelId = nozzle.Id,
                NozzleModel = nozzle,
                HotendModelId = hotend.Id,
                HotendModel = hotend,
                ExtruderModelId = extruder.Id,
                ExtruderModel = extruder,
                SupportedMaterials = ["ABS"],
            };
            _ = db.NozzleMaterials.Add(nozzleMaterial);
            _ = db.NozzleModelDefinitions.Add(nozzle);
            _ = db.HotendModelDefinitions.Add(hotend);
            _ = db.ExtruderModelDefinitions.Add(extruder);
            _ = db.PrinterModelToolheads.Add(toolhead);
            _ = await db.SaveChangesAsync();
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
                CalibrationHasHeatedChamber = false,
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
