using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services.Calibration.Generation;

/// <summary>
/// Fail-closed tests for the specification compiler: an unsupported tuple, an incomplete authoritative
/// context, a stale snapshot, a changed profile digest or an out-of-limit request must be rejected.
/// </summary>
public sealed class CalibrationSpecificationCompilerTests
{
    [Fact]
    public void Compile_WithCompleteAuthoritativeContext_ProducesCanonicalHashAndOperationId()
    {
        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                CalibrationGenerationTestData.Context(),
                new TemperatureCalibrationOptions());

        _ = result.Problems.Should().BeEmpty();
        _ = result.Value!.Document.OperationId.Should().Be("op-0000000000000001");
        _ = result.Value.Document.CalibrationKind.Should().Be("temperature");
        _ = result.Value.Sha256.Should().HaveLength(64);
    }

    [Theory]
    [InlineData("Marlin", "Klipper", "OrcaSlicer", "upstream", CalibrationContractConstants.SlicerVersion, "firmware_family_unsupported")]
    [InlineData("Klipper", "Marlin", "OrcaSlicer", "upstream", CalibrationContractConstants.SlicerVersion, "gcode_dialect_unsupported")]
    [InlineData("Klipper", "Klipper", "PrusaSlicer", "upstream", CalibrationContractConstants.SlicerVersion, "slicer_engine_unsupported")]
    [InlineData("Klipper", "Klipper", "OrcaSlicer", "vendor-fork", CalibrationContractConstants.SlicerVersion, "slicer_distribution_unsupported")]
    [InlineData("Klipper", "Klipper", "OrcaSlicer", "upstream", "2.2.0", "slicer_version_unsupported")]
    public void Compile_WithTupleElementOutsideTheSupportedTuple_RejectsFailClosed(
        string firmwareFamily,
        string dialect,
        string engine,
        string distribution,
        string version,
        string expectedCode)
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context() with
        {
            Compatibility = new CalibrationCompatibilityIdentity(
                firmwareFamily,
                dialect,
                engine,
                distribution,
                version,
                CalibrationGenerationTestData.ContainerDigest,
                CalibrationGenerationTestData.BinaryDigest,
                "orca-json"),
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.IsValid.Should().BeFalse();
        _ = result.Problems.Select(problem => problem.Code).Should().Contain(expectedCode);
    }

    [Fact]
    public void Compile_WithMissingContainerDigest_ReturnsExplicitDependencyReason()
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context() with
        {
            Compatibility = new CalibrationCompatibilityIdentity(
                "Klipper",
                "Klipper",
                "OrcaSlicer",
                "upstream",
                CalibrationContractConstants.SlicerVersion,
                null,
                CalibrationGenerationTestData.BinaryDigest,
                "orca-json"),
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("slicer_container_digest_missing");
    }

    [Fact]
    public void Compile_WithMissingBinaryDigest_ReturnsExplicitDependencyReason()
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context() with
        {
            Compatibility = new CalibrationCompatibilityIdentity(
                "Klipper",
                "Klipper",
                "OrcaSlicer",
                "upstream",
                CalibrationContractConstants.SlicerVersion,
                CalibrationGenerationTestData.ContainerDigest,
                "   ",
                "orca-json"),
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("slicer_binary_digest_missing");
    }

    [Fact]
    public void Compile_WithUnverifiedFirmware_RejectsWithoutInferringFromBackend()
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context() with
        {
            Firmware = new CalibrationFirmwareContext(
                "Klipper",
                "v0.12.0",
                "printer",
                "Klipper",
                false,
                CalibrationGenerationTestData.CapturedAtUtc),
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should().Contain("firmware_unverified");
    }

    [Fact]
    public void Compile_WithUnknownFirmwareDetectionSource_Rejects()
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context() with
        {
            Firmware = new CalibrationFirmwareContext(
                "Klipper",
                "v0.12.0",
                "unknown",
                "Klipper",
                true,
                CalibrationGenerationTestData.CapturedAtUtc),
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("firmware_detection_source_missing");
    }

    [Fact]
    public void Compile_WithStalePrinterConfigurationRevision_Rejects()
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context() with
        {
            CurrentPrinterConfigurationRevision = 43,
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("printer_configuration_stale");
    }

    [Fact]
    public void Compile_WithSnapshotOutsideTheFreshnessWindow_Rejects()
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context() with
        {
            SnapshotCapturedAtUtc = CalibrationGenerationTestData.NowUtc.AddDays(-3),
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should().Contain("snapshot_stale");
    }

    [Fact]
    public void Compile_WithMissingSnapshotTimestamp_DoesNotSynthesizeFreshness()
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context() with
        {
            SnapshotCapturedAtUtc = null,
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("snapshot_timestamp_missing");
    }

    [Fact]
    public void Compile_WithChangedProfileDigest_Rejects()
    {
        CalibrationGenerationContext baseline = CalibrationGenerationTestData.Context();
        CalibrationGenerationContext context = baseline with
        {
            Profiles = new CalibrationProfileTriplet(
                baseline.Profiles.Machine! with { Sha256 = new string('a', 64) },
                baseline.Profiles.Process,
                baseline.Profiles.Filament),
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should().Contain("profile_hash_mismatch");
    }

    [Fact]
    public void Compile_WithMissingExactProfileJson_Rejects()
    {
        CalibrationGenerationContext baseline = CalibrationGenerationTestData.Context();
        CalibrationGenerationContext context = baseline with
        {
            Profiles = new CalibrationProfileTriplet(
                baseline.Profiles.Machine,
                baseline.Profiles.Process! with { ExactJson = null },
                baseline.Profiles.Filament),
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should().Contain("profile_json_missing");
    }

    [Fact]
    public void Compile_WithMissingNozzleCeiling_DoesNotSynthesizeALimit()
    {
        CalibrationGenerationContext baseline = CalibrationGenerationTestData.Context();
        CalibrationGenerationContext context = baseline with
        {
            Toolhead = baseline.Toolhead with
            {
                NozzleMaxTemperatureCelsius = null,
                HotendMaxTemperatureCelsius = null,
            },
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should().Contain("nozzle_limit_missing");
    }

    [Fact]
    public void Compile_WithMissingBuildVolume_DoesNotSynthesizeGeometry()
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context() with
        {
            Bed = new CalibrationBedGeometry(null, null, null, null, null, [], []),
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should().Contain("build_volume_missing");
    }

    [Fact]
    public void Compile_WithMissingOperationId_Rejects()
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context() with
        {
            OperationId = "  ",
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should().Contain("operation_id_missing");
    }

    [Fact]
    public void Compile_WithUnsupportedDefinitionVersion_Rejects()
    {
        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                CalibrationGenerationTestData.Context(),
                new TemperatureCalibrationOptions { DefinitionVersion = "2.0" });

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("method_definition_version_unsupported");
    }

    [Fact]
    public void Compile_WithTemperatureAboveTheNozzleCeiling_Rejects()
    {
        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                CalibrationGenerationTestData.Context(),
                new TemperatureCalibrationOptions
                {
                    StartCelsius = 400,
                    EndCelsius = 380,
                    StepCelsius = 5,
                });

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("temperature_above_nozzle_limit");
    }

    [Fact]
    public void Compile_WithTemperatureBelowTheSafeMinimum_Rejects()
    {
        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                CalibrationGenerationTestData.Context(),
                new TemperatureCalibrationOptions
                {
                    StartCelsius = 140,
                    EndCelsius = 100,
                    StepCelsius = 10,
                });

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("temperature_below_safe_minimum");
    }

    [Fact]
    public void Compile_WithAscendingTemperatureTower_RejectsTheMalformedSweep()
    {
        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                CalibrationGenerationTestData.Context(),
                new TemperatureCalibrationOptions
                {
                    StartCelsius = 200,
                    EndCelsius = 240,
                    StepCelsius = 5,
                });

        _ = result.Problems.Select(problem => problem.Code).Should().Contain("sweep_invalid");
    }

    [Fact]
    public void Compile_WithFlowRatioOutsideTheSafeRange_Rejects()
    {
        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                CalibrationGenerationTestData.Context(),
                new FlowRatioCalibrationOptions(CalibrationMethod.FlowRatioHighRange)
                {
                    StartRatio = 0.2m,
                    EndRatio = 0.4m,
                    StepRatio = 0.1m,
                });

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("flow_ratio_out_of_range");
    }

    [Fact]
    public void Compile_WithPressureAdvanceAboveTheDirectDriveCeiling_Rejects()
    {
        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                CalibrationGenerationTestData.Context(directDrive: true),
                new PressureAdvanceTowerCalibrationOptions
                {
                    StartPressureAdvance = 0m,
                    EndPressureAdvance = 1.5m,
                    StepPressureAdvance = 0.25m,
                });

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("pressure_advance_out_of_range");
    }

    [Fact]
    public void Compile_WithRetractionAboveTheDirectDriveCeiling_Rejects()
    {
        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                CalibrationGenerationTestData.Context(directDrive: true),
                new RetractionCalibrationOptions
                {
                    StartLengthMillimeters = 0m,
                    EndLengthMillimeters = 8m,
                    StepLengthMillimeters = 2m,
                });

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("retraction_out_of_range");
    }

    [Fact]
    public void Compile_WithVolumetricSpeedAboveTheAuthoritativeCeiling_Rejects()
    {
        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                CalibrationGenerationTestData.Context(),
                new MaximumVolumetricSpeedCalibrationOptions
                {
                    StartCubicMillimetersPerSecond = 10m,
                    EndCubicMillimetersPerSecond = 40m,
                    StepCubicMillimetersPerSecond = 10m,
                });

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("volumetric_flow_out_of_range");
    }

    [Fact]
    public void Compile_WithChamberTemperatureButNoHeatedChamber_Rejects()
    {
        CalibrationGenerationContext baseline = CalibrationGenerationTestData.Context();
        CalibrationGenerationContext context = baseline with
        {
            Filament = baseline.Filament with { ChamberTemperatureCelsius = 50 },
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("chamber_temperature_unsupported");
    }

    [Fact]
    public void Compile_WithFootprintOutsideThePrintablePolygon_Rejects()
    {
        CalibrationGenerationContext baseline = CalibrationGenerationTestData.Context();
        CalibrationGenerationContext context = baseline with
        {
            Bed = baseline.Bed with
            {
                PrintablePolygon =
                [
                    new CalibrationBedPoint(0m, 0m),
                    new CalibrationBedPoint(40m, 0m),
                    new CalibrationBedPoint(40m, 40m),
                    new CalibrationBedPoint(0m, 40m),
                ],
            },
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("footprint_outside_printable_polygon");
    }

    [Fact]
    public void Compile_WithFootprintOverlappingAnExcludedRegion_Rejects()
    {
        CalibrationGenerationContext baseline = CalibrationGenerationTestData.Context();
        CalibrationGenerationContext context = baseline with
        {
            Bed = baseline.Bed with
            {
                ExcludedRegions =
                [
                    new CalibrationExcludedRegion(
                        "purge-bucket",
                        [
                            new CalibrationBedPoint(100m, 100m),
                            new CalibrationBedPoint(140m, 100m),
                            new CalibrationBedPoint(140m, 140m),
                            new CalibrationBedPoint(100m, 140m),
                        ]),
                ],
            },
        };

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions());

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("footprint_inside_excluded_region");
    }

    [Fact]
    public void Compile_FinalVerificationWithoutLinkedAsset_Rejects()
    {
        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                CalibrationGenerationTestData.Context(),
                new FinalVerificationCalibrationOptions
                {
                    Model3DId = CalibrationGenerationTestData.ModelId,
                });

        _ = result.Problems.Select(problem => problem.Code).Should().Contain("linked_asset_missing");
    }

    [Fact]
    public void Compile_FinalVerificationWithMismatchedAssetHash_Rejects()
    {
        CalibrationGenerationContext context = CalibrationGenerationPipeline.ContextFor(
            CalibrationMethod.FinalVerification);

        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new FinalVerificationCalibrationOptions
                {
                    Model3DId = CalibrationGenerationTestData.ModelId,
                    ExpectedSha256 = new string('b', 64),
                });

        _ = result.Problems.Select(problem => problem.Code).Should().Contain("linked_asset_mismatch");
    }

    [Fact]
    public void VerifyStillCurrent_WithUnchangedContext_ReturnsNoProblems()
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context();
        CalibrationSpecificationCompiler compiler = CalibrationGenerationTestData.Compiler();
        CalibrationSpecification specification =
            compiler.Compile(context, new TemperatureCalibrationOptions()).Value!;

        IReadOnlyList<CalibrationGenerationProblem> problems =
            compiler.VerifyStillCurrent(context, specification);

        _ = problems.Should().BeEmpty();
    }

    [Fact]
    public void VerifyStillCurrent_WithAdvancedPrinterRevision_ReportsStaleConfiguration()
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context();
        CalibrationSpecificationCompiler compiler = CalibrationGenerationTestData.Compiler();
        CalibrationSpecification specification =
            compiler.Compile(context, new TemperatureCalibrationOptions()).Value!;

        IReadOnlyList<CalibrationGenerationProblem> problems = compiler.VerifyStillCurrent(
            context with { CurrentPrinterConfigurationRevision = 99 },
            specification);

        _ = problems.Select(problem => problem.Code).Should().Contain("printer_configuration_stale");
    }

    [Fact]
    public void VerifyStillCurrent_WithReplacedProfile_ReportsProfileHashMismatch()
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context();
        CalibrationSpecificationCompiler compiler = CalibrationGenerationTestData.Compiler();
        CalibrationSpecification specification =
            compiler.Compile(context, new TemperatureCalibrationOptions()).Value!;

        string replacedJson = CalibrationGenerationTestData.FilamentProfileJson()
            .Replace("\"220\"", "\"215\"", StringComparison.Ordinal);
        CalibrationGenerationContext changed = context with
        {
            Profiles = new CalibrationProfileTriplet(
                context.Profiles.Machine,
                context.Profiles.Process,
                CalibrationGenerationTestData.Profile(
                    CalibrationGenerationTestData.FilamentProfileId,
                    "filament",
                    "PF Filament",
                    replacedJson)),
        };

        IReadOnlyList<CalibrationGenerationProblem> problems =
            compiler.VerifyStillCurrent(changed, specification);

        _ = problems.Select(problem => problem.Code).Should().Contain("profile_hash_mismatch");
    }
}
