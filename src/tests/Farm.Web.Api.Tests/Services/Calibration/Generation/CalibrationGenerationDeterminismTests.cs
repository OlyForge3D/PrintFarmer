using System.Text.Json;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services.Calibration.Generation;

/// <summary>
/// Verifies that the calibration generation pipeline is deterministic: for every supported method,
/// repeated invocations with identical inputs within the same build must produce identical canonical
/// specification, plan, G-code, and manifest digests.
/// </summary>
/// <remarks>
/// This suite does NOT verify parity with OrcaSlicer's own output — it does not compare generated
/// artifacts against anything OrcaSlicer itself produces. It only guarantees that PrintFarmer's own
/// generation pipeline is reproducible across repeated calls. See
/// https://github.com/OlyForge3D/PrintFarmer/issues/1926 for the OrcaSlicer parity gap this suite
/// does not close.
/// </remarks>
public sealed class CalibrationGenerationDeterminismTests
{
    private static readonly CalibrationMethod[] AllMethods =
    [
        CalibrationMethod.Temperature,
        CalibrationMethod.FlowRatioCoarse,
        CalibrationMethod.FlowRatioFine,
        CalibrationMethod.FlowRatioHighRange,
        CalibrationMethod.PressureAdvanceTower,
        CalibrationMethod.PressureAdvanceLine,
        CalibrationMethod.PressureAdvancePattern,
        CalibrationMethod.FlowVerification,
        CalibrationMethod.Retraction,
        CalibrationMethod.MaximumVolumetricSpeed,
        CalibrationMethod.Shrinkage,
        CalibrationMethod.FinalVerification,
    ];

    public static TheoryData<CalibrationMethod> SupportedMethods()
    {
        TheoryData<CalibrationMethod> data = [];
        foreach (CalibrationMethod method in AllMethods)
        {
            data.Add(method);
        }

        return data;
    }

    public static TheoryData<CalibrationMethod, decimal, bool> MethodNozzleVariants()
    {
        TheoryData<CalibrationMethod, decimal, bool> data = [];
        foreach (CalibrationMethod method in AllMethods)
        {
            data.Add(method, 0.4m, true);
            data.Add(method, 0.6m, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SupportedMethods))]
    public void Compile_ForEverySupportedMethod_ProducesCanonicalSpecificationAndSegments(
        CalibrationMethod method)
    {
        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationPipeline.CompileSpecification(method);

        _ = result.Problems.Should().BeEmpty();
        _ = result.IsValid.Should().BeTrue();
        CalibrationSpecificationDocument document = result.Value!.Document;
        _ = document.Method.Should().Be(CalibrationMethodNames.ToName(method));
        _ = document.Segments.Should().NotBeEmpty();
        _ = document.Sweep.SegmentCount.Should().Be(document.Segments.Count);
        _ = result.Value.Sha256.Should()
            .Be(CalibrationCanonicalJson.ComputeTextSha256(result.Value.CanonicalJson));
    }

    [Theory]
    [MemberData(nameof(MethodNozzleVariants))]
    public void Generate_ForIdenticalInputs_ProducesIdenticalSpecificationPlanGcodeAndManifestHashes(
        CalibrationMethod method,
        decimal nozzleDiameter,
        bool directDrive)
    {
        CalibrationGenerationPipeline.Result first =
            CalibrationGenerationPipeline.Run(method, nozzleDiameter, directDrive);
        CalibrationGenerationPipeline.Result second =
            CalibrationGenerationPipeline.Run(method, nozzleDiameter, directDrive);

        _ = first.Problems.Should().BeEmpty();
        _ = second.Problems.Should().BeEmpty();
        _ = second.Specification!.Sha256.Should().Be(first.Specification!.Sha256);
        _ = second.Plan!.ManifestSha256.Should().Be(first.Plan!.ManifestSha256);
        _ = second.Program!.Sha256.Should().Be(first.Program!.Sha256);
        _ = second.Annotated!.GcodeSha256.Should().Be(first.Annotated!.GcodeSha256);
        _ = second.Annotated.ManifestSha256.Should().Be(first.Annotated.ManifestSha256);
        _ = second.Annotated.Gcode.Should().Be(first.Annotated.Gcode);
    }

    [Theory]
    [MemberData(nameof(SupportedMethods))]
    public void Generate_ForDifferentNozzleDiameters_ProducesDifferentGcodeHashes(
        CalibrationMethod method)
    {
        CalibrationGenerationPipeline.Result small =
            CalibrationGenerationPipeline.Run(method, 0.4m, directDrive: true);
        CalibrationGenerationPipeline.Result large =
            CalibrationGenerationPipeline.Run(method, 0.6m, directDrive: true);

        _ = small.Problems.Should().BeEmpty();
        _ = large.Problems.Should().BeEmpty();
        _ = large.Annotated!.GcodeSha256.Should().NotBe(small.Annotated!.GcodeSha256);
    }

    [Theory]
    [MemberData(nameof(SupportedMethods))]
    public void Generate_ForDifferentToolheadIndex_ProducesDifferentSpecificationHash(
        CalibrationMethod method)
    {
        CalibrationGenerationPipeline.Result primary =
            CalibrationGenerationPipeline.Run(method, 0.4m, directDrive: true, toolheadIndex: 0);
        CalibrationGenerationPipeline.Result secondary =
            CalibrationGenerationPipeline.Run(method, 0.4m, directDrive: true, toolheadIndex: 1);

        _ = primary.Problems.Should().BeEmpty();
        _ = secondary.Problems.Should().BeEmpty();
        _ = secondary.Specification!.Sha256.Should().NotBe(primary.Specification!.Sha256);
    }

    [Theory]
    [MemberData(nameof(SupportedMethods))]
    public void Annotate_ForEverySupportedMethod_RecordsSegmentOffsetsThatAddressTheEmittedMarkers(
        CalibrationMethod method)
    {
        CalibrationGenerationPipeline.Result run =
            CalibrationGenerationPipeline.Run(method, 0.4m, directDrive: true);

        _ = run.Problems.Should().BeEmpty();
        AnnotatedCalibrationGcode annotated = run.Annotated!;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(annotated.Gcode);

        _ = annotated.Manifest.Segments.Should()
            .HaveCount(run.Specification!.Document.Segments.Count);
        foreach (CalibrationSegmentAnnotation segment in annotated.Manifest.Segments)
        {
            string atOffset = System.Text.Encoding.UTF8.GetString(
                bytes,
                segment.StartByteOffset,
                Math.Min(
                    CalibrationGcodeMarkers.SegmentBegin.Length,
                    bytes.Length - segment.StartByteOffset));
            _ = atOffset.Should().Be(CalibrationGcodeMarkers.SegmentBegin);
            _ = segment.EndLine.Should().BeGreaterThan(segment.StartLine);
            _ = segment.EndByteOffset.Should().BeGreaterThan(segment.StartByteOffset);
        }
    }

    [Theory]
    [MemberData(nameof(SupportedMethods))]
    public void Annotate_ForEverySupportedMethod_EmitsProvenanceHeaderAndFinalDigest(
        CalibrationMethod method)
    {
        CalibrationGenerationPipeline.Result run =
            CalibrationGenerationPipeline.Run(method, 0.4m, directDrive: true);

        _ = run.Problems.Should().BeEmpty();
        AnnotatedCalibrationGcode annotated = run.Annotated!;

        _ = annotated.Gcode.Should().StartWith(CalibrationGcodeMarkers.HeaderPrefix);
        _ = annotated.Gcode.Should().Contain($"{CalibrationGcodeMarkers.HeaderPrefix} projectId=");
        _ = annotated.Gcode.Should().Contain(
            $"{CalibrationGcodeMarkers.HeaderPrefix} slicerContainerDigest=");
        _ = annotated.Manifest.GcodeSha256.Should()
            .Be(CalibrationCanonicalJson.ComputeTextSha256(annotated.Gcode));
        _ = annotated.Manifest.FirmwareFamily.Should().Be("Klipper");
        _ = annotated.Manifest.GcodeDialect.Should().Be("Klipper");
        _ = annotated.Manifest.SlicerVersion.Should().Be(CalibrationContractConstants.SlicerVersion);
        _ = annotated.Manifest.ResetCommands.Should().Contain("TURN_OFF_HEATERS");
        _ = annotated.Manifest.ResetCommands.Should().Contain("M84");
    }

    [Theory]
    [MemberData(nameof(SupportedMethods))]
    public void Validate_ForEveryGeneratedProgram_PassesStaticSafetyAtEveryCheckpoint(
        CalibrationMethod method)
    {
        CalibrationGenerationPipeline.Result run =
            CalibrationGenerationPipeline.Run(method, 0.4m, directDrive: true);
        _ = run.Problems.Should().BeEmpty();

        CalibrationSafetyCheckpoint[] checkpoints =
        [
            CalibrationSafetyCheckpoint.BeforeArtifactCompletion,
            CalibrationSafetyCheckpoint.BeforePromotion,
            CalibrationSafetyCheckpoint.BeforeQueueing,
            CalibrationSafetyCheckpoint.BeforeStart,
        ];

        foreach (CalibrationSafetyCheckpoint checkpoint in checkpoints)
        {
            CalibrationGenerationResult<CalibrationGcodeSafetyReport> report =
                CalibrationGenerationPipeline.Validate(run, checkpoint);
            _ = report.Problems.Should().BeEmpty();
            _ = report.IsValid.Should().BeTrue();
        }
    }

    [Theory]
    [MemberData(nameof(SupportedMethods))]
    public void Generate_ForEverySupportedMethod_NeverEmitsTuningTower(CalibrationMethod method)
    {
        CalibrationGenerationPipeline.Result run =
            CalibrationGenerationPipeline.Run(method, 0.4m, directDrive: true);

        _ = run.Problems.Should().BeEmpty();
        _ = run.Annotated!.Gcode.Should().NotContain(KlipperCalibrationCommands.TuningTower);
    }

    [Theory]
    [MemberData(nameof(SupportedMethods))]
    public void Generate_ForEverySupportedMethod_EmitsOnlyAllowlistedCommands(
        CalibrationMethod method)
    {
        CalibrationGenerationPipeline.Result run =
            CalibrationGenerationPipeline.Run(method, 0.4m, directDrive: true);
        _ = run.Problems.Should().BeEmpty();

        List<string> unexpected = [];
        foreach (string rawLine in run.Annotated!.Gcode.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            int end = line.IndexOf(' ', StringComparison.Ordinal);
            string command = (end < 0 ? line : line[..end]).ToUpperInvariant();
            if (!KlipperCalibrationCommands.IsAllowed(command))
            {
                unexpected.Add(command);
            }
        }

        _ = unexpected.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(SupportedMethods))]
    public void Generate_ForEverySupportedMethod_UsesInvariantFormattingAndUnixNewlines(
        CalibrationMethod method)
    {
        CalibrationGenerationPipeline.Result run =
            CalibrationGenerationPipeline.Run(method, 0.4m, directDrive: true);

        _ = run.Problems.Should().BeEmpty();
        _ = run.Annotated!.Gcode.Should().NotContain("\r");
        _ = run.Annotated.Gcode.Should().NotContain(",0");
        _ = run.Annotated.Gcode.Should().EndWith($"{CalibrationGcodeMarkers.ProgramEnd}\n");
    }

    [Fact]
    public void Compile_TemperatureTowerAtEdgeOfNozzleCeiling_ProducesSegmentsWithinLimit()
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context();
        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new TemperatureCalibrationOptions
                {
                    StartCelsius = 300,
                    EndCelsius = 150,
                    StepCelsius = 25,
                });

        _ = result.Problems.Should().BeEmpty();
        _ = result.Value!.Document.Segments.Should().HaveCount(7);
        _ = result.Value.Document.Segments[0].Value.Should().Be(300m);
        _ = result.Value.Document.Segments[^1].Value.Should().Be(150m);
    }

    [Fact]
    public void Compile_FlowRatioCoarseWithoutOptions_DerivesDefaultsFromAuthoritativeBaseline()
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context();
        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationTestData.Compiler().Compile(
                context,
                new FlowRatioCalibrationOptions(CalibrationMethod.FlowRatioCoarse));

        _ = result.Problems.Should().BeEmpty();
        _ = result.Value!.Document.Sweep.Start.Should().Be(0.90m);
        _ = result.Value.Document.Sweep.End.Should().Be(1.10m);
        _ = result.Value.Document.Sweep.Unit.Should().Be(CalibrationUnits.Ratio);
    }

    [Fact]
    public void Compile_PressureAdvanceDefaults_DifferBetweenDirectDriveAndBowden()
    {
        CalibrationGenerationResult<CalibrationSpecification> direct =
            CalibrationGenerationTestData.Compiler().Compile(
                CalibrationGenerationTestData.Context(directDrive: true),
                new PressureAdvanceTowerCalibrationOptions());
        CalibrationGenerationResult<CalibrationSpecification> bowden =
            CalibrationGenerationTestData.Compiler().Compile(
                CalibrationGenerationTestData.Context(directDrive: false),
                new PressureAdvanceTowerCalibrationOptions());

        _ = direct.Problems.Should().BeEmpty();
        _ = bowden.Problems.Should().BeEmpty();
        _ = direct.Value!.Document.Sweep.End.Should().Be(0.10m);
        _ = bowden.Value!.Document.Sweep.End.Should().Be(1.00m);
    }

    [Fact]
    public void Compile_SegmentZRanges_FollowTheResolvedLayerHeight()
    {
        CalibrationGenerationResult<CalibrationSpecification> result =
            CalibrationGenerationPipeline.CompileSpecification(CalibrationMethod.Temperature);

        _ = result.Problems.Should().BeEmpty();
        CalibrationSegmentSpecification first = result.Value!.Document.Segments[0];
        _ = first.StartLayer.Should().Be(1);
        _ = first.EndLayer.Should().Be(CalibrationSweepResolver.LayersPerBand);
        _ = first.StartZMillimeters.Should().Be(0.2m);
        _ = first.EndZMillimeters.Should().Be(2.0m);
    }

    [Fact]
    public void Compile_FinalVerification_PreservesLinkedAssetIdentityAndHash()
    {
        CalibrationGenerationPipeline.Result run =
            CalibrationGenerationPipeline.Run(CalibrationMethod.FinalVerification, 0.4m, true);

        _ = run.Problems.Should().BeEmpty();
        _ = run.Specification!.Document.ImportedAsset.Should().NotBeNull();
        _ = run.Annotated!.Manifest.Model3DId.Should()
            .Be(CalibrationGenerationTestData.ModelId);
        _ = run.Annotated.Manifest.BodySource.Should()
            .Be(CalibrationGcodeBodySource.SlicedFromLinkedAsset.ToString());
        _ = run.Annotated.Gcode.Should().Contain("SOURCE=sliced-asset");
    }

    [Fact]
    public void Compile_ServerGeneratedMethods_DeclareServerGeneratedBodySource()
    {
        CalibrationGenerationPipeline.Result run =
            CalibrationGenerationPipeline.Run(CalibrationMethod.Temperature, 0.4m, true);

        _ = run.Problems.Should().BeEmpty();
        _ = run.Annotated!.Manifest.BodySource.Should()
            .Be(CalibrationGcodeBodySource.ServerGenerated.ToString());
    }

    [Fact]
    public void Plan_ForEverySupportedMethod_EmitsOnlyAllowlistedNativeOverrides()
    {
        foreach (CalibrationMethod method in AllMethods)
        {
            CalibrationGenerationPipeline.Result run =
                CalibrationGenerationPipeline.Run(method, 0.4m, directDrive: true);
            _ = run.Problems.Should().BeEmpty();

            foreach (OrcaSettingOverride setting in run.Plan!.Manifest.Overrides)
            {
                _ = OrcaCalibrationPlanCompiler.AllowedOverrideKeys.Should()
                    .Contain(setting.Key);
            }
        }
    }

    [Fact]
    public void Plan_ForEverySupportedMethod_PinsUpstreamVersionAndBothDigests()
    {
        CalibrationGenerationPipeline.Result run =
            CalibrationGenerationPipeline.Run(CalibrationMethod.Temperature, 0.4m, true);

        _ = run.Problems.Should().BeEmpty();
        _ = run.Plan!.Manifest.SlicerEngine.Should().Be("OrcaSlicer");
        _ = run.Plan.Manifest.SlicerDistribution.Should().Be("upstream");
        _ = run.Plan.Manifest.SlicerVersion.Should().Be(CalibrationContractConstants.SlicerVersion);
        _ = run.Plan.Manifest.SlicerContainerDigest.Should()
            .Be(CalibrationGenerationTestData.ContainerDigest);
        _ = run.Plan.Manifest.SlicerBinarySha256.Should()
            .Be(CalibrationGenerationTestData.BinaryDigest);
        _ = run.Plan.MachineProfile.SourceExactJson.Should()
            .Be(run.Specification!.Document.Profiles.Machine!.ExactJson);
    }

    [Theory]
    [MemberData(nameof(SupportedMethods))]
    public void Plan_ForEverySupportedMethod_KeepsBaselinesExactAndDerivesCanonicalEffectiveDocuments(
        CalibrationMethod method)
    {
        CalibrationGenerationPipeline.Result run =
            CalibrationGenerationPipeline.Run(method, 0.4m, directDrive: true);
        _ = run.Problems.Should().BeEmpty();

        CalibrationProfileTriplet profiles = run.Specification!.Document.Profiles;
        (OrcaPlanProfile Plan, CalibrationExactProfile? Source)[] pairs =
        [
            (run.Plan!.MachineProfile, profiles.Machine),
            (run.Plan.ProcessProfile, profiles.Process),
            (run.Plan.FilamentProfile, profiles.Filament),
        ];

        foreach ((OrcaPlanProfile plan, CalibrationExactProfile? source) in pairs)
        {
            _ = plan.SourceExactJson.Should().Be(source!.ExactJson);
            _ = plan.SourceSha256.Should().Be(source.Sha256);
            _ = plan.EffectiveSha256.Should()
                .Be(CalibrationCanonicalJson.ComputeTextSha256(plan.EffectiveJson));

            using JsonDocument effective = JsonDocument.Parse(plan.EffectiveJson);
            foreach (JsonProperty property in effective.RootElement.EnumerateObject())
            {
                if (!OrcaProfileCommandKeys.IsForbidden(property.Name))
                {
                    continue;
                }

                _ = plan.NeutralizedKeys.Should().Contain(property.Name);
                bool empty = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString()!.Length == 0,
                    JsonValueKind.Array => property.Value.GetArrayLength() == 0,
                    _ => false,
                };
                _ = empty.Should()
                    .BeTrue($"{property.Name} must carry no value in the effective document");
            }

            // Every recorded name is a key the baseline really declared, and the rule really forbids.
            using JsonDocument baseline = JsonDocument.Parse(plan.SourceExactJson);
            foreach (string neutralized in plan.NeutralizedKeys)
            {
                _ = OrcaProfileCommandKeys.IsForbidden(neutralized).Should().BeTrue();
                _ = baseline.RootElement.TryGetProperty(neutralized, out _).Should().BeTrue();
            }

            _ = plan.NeutralizedKeys.Should().BeInAscendingOrder(StringComparer.Ordinal);
        }
    }

    [Fact]
    public void Canonicalize_ForReorderedButEquivalentDocuments_ProducesTheSameDigest()
    {
        string first = CalibrationCanonicalJson.Serialize(new { b = 2, a = 1 });
        string second = CalibrationCanonicalJson.Serialize(new { a = 1, b = 2 });

        _ = second.Should().Be(first);
        _ = CalibrationCanonicalJson.ComputeTextSha256(second).Should()
            .Be(CalibrationCanonicalJson.ComputeTextSha256(first));
    }
}
