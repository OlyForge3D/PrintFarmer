using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services.Calibration.Generation;

/// <summary>
/// Adversarial tests for the upstream-Orca plan compiler.
/// </summary>
public sealed class OrcaCalibrationPlanCompilerTests
{
    private static (CalibrationSpecification Specification, CalibrationValidatedModel Model)
        Prepare(CalibrationGenerationContext? context = null)
    {
        CalibrationSpecification specification = CalibrationGenerationTestData.Compiler()
            .Compile(
                context ?? CalibrationGenerationTestData.Context(),
                new TemperatureCalibrationOptions())
            .Value!;
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
        return (specification, model);
    }

    private static IReadOnlyList<string> Codes(
        CalibrationGenerationResult<OrcaCalibrationPlan> result) =>
        result.Problems.Select(problem => problem.Code).ToArray();

    [Fact]
    public void Compile_WithVerifiedProfiles_CarriesExactNativeJsonVerbatim()
    {
        (CalibrationSpecification specification, CalibrationValidatedModel model) = Prepare();

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = result.Problems.Should().BeEmpty();
        _ = result.Value!.MachineProfile.ExactJson.Should()
            .Be(specification.Document.Profiles.Machine!.ExactJson);
        _ = result.Value.ProcessProfile.ExactJson.Should()
            .Be(specification.Document.Profiles.Process!.ExactJson);
        _ = result.Value.FilamentProfile.ExactJson.Should()
            .Be(specification.Document.Profiles.Filament!.ExactJson);
        _ = result.Value.ManifestSha256.Should()
            .Be(CalibrationCanonicalJson.ComputeTextSha256(result.Value.ManifestJson));
    }

    [Fact]
    public void Compile_ProducesOverridesOrderedByKeyForDeterministicManifests()
    {
        (CalibrationSpecification specification, CalibrationValidatedModel model) = Prepare();

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = result.Problems.Should().BeEmpty();
        IReadOnlyList<OrcaSettingOverride> overrides = result.Value!.Manifest.Overrides;
        _ = overrides.Select(setting => setting.Key).Should()
            .BeInAscendingOrder(StringComparer.Ordinal);
        _ = overrides.Should().OnlyContain(
            setting => OrcaCalibrationPlanCompiler.AllowedOverrideKeys.Contains(setting.Key));
    }

    [Fact]
    public void Compile_WithMachineProfileNozzleMismatch_Rejects()
    {
        CalibrationGenerationContext baseline = CalibrationGenerationTestData.Context();
        string mismatched = CalibrationGenerationTestData.MachineProfileJson(0.8m);
        CalibrationGenerationContext context = baseline with
        {
            Profiles = new CalibrationProfileTriplet(
                CalibrationGenerationTestData.Profile(
                    CalibrationGenerationTestData.MachineProfileId,
                    "machine",
                    "PF Machine",
                    mismatched),
                baseline.Profiles.Process,
                baseline.Profiles.Filament),
        };

        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = Codes(result).Should().Contain("plan_nozzle_mismatch");
    }

    [Fact]
    public void Compile_WithProfileDeclaringInheritance_Rejects()
    {
        CalibrationGenerationContext context = ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"inherits\":\"Generic Klipper\"," +
            "\"nozzle_diameter\":[\"0.4\"]}");

        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = Codes(result).Should().Contain("plan_profile_inheritance_unsupported");
    }

    [Theory]
    [InlineData("machine_start_gcode")]
    [InlineData("machine_end_gcode")]
    [InlineData("layer_change_gcode")]
    public void Compile_WithProfileCarryingArbitraryCommandFields_Rejects(string key)
    {
        CalibrationGenerationContext context = ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"],\"" + key +
            "\":\"G28 ; then something else\"}");

        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = Codes(result).Should().Contain("plan_profile_unsafe_command");
    }

    [Fact]
    public void Compile_WithProfileCarryingPostProcessingCommand_Rejects()
    {
        CalibrationGenerationContext context = ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"]," +
            "\"post_process\":[\"/bin/sh -c backup\"]}");

        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = Codes(result).Should().Contain("profile_contains_unsafe_command");
    }

    [Fact]
    public void Compile_WithProfileCarryingCredential_Rejects()
    {
        CalibrationGenerationContext context = ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"]," +
            "\"api_key\":\"super-secret-value\"}");

        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = Codes(result).Should().Contain("profile_contains_credential");
    }

    [Fact]
    public void Compile_WithProfileCarryingPrivateUrl_Rejects()
    {
        CalibrationGenerationContext context = ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"]," +
            "\"service_url\":\"http://10.0.0.42/api\"}");

        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = Codes(result).Should().Contain("profile_contains_private_url");
    }

    [Fact]
    public void Compile_WithChangedProfileJsonAfterCompilation_RejectsOnDigestMismatch()
    {
        (CalibrationSpecification specification, CalibrationValidatedModel model) = Prepare();
        CalibrationSpecificationDocument document = specification.Document;
        CalibrationSpecificationDocument tamperedDocument = document with
        {
            Profiles = new CalibrationProfileTriplet(
                document.Profiles.Machine! with
                {
                    ExactJson = document.Profiles.Machine.ExactJson + " ",
                },
                document.Profiles.Process,
                document.Profiles.Filament),
        };
        CalibrationSpecification tampered = new(
            tamperedDocument,
            specification.CanonicalJson,
            specification.Sha256);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(tampered, model);

        _ = Codes(result).Should().Contain("profile_hash_mismatch");
    }

    [Fact]
    public void Compile_WithSpecificationDigestThatDoesNotMatchItsJson_Rejects()
    {
        (CalibrationSpecification specification, CalibrationValidatedModel model) = Prepare();
        CalibrationSpecification tampered = new(
            specification.Document,
            specification.CanonicalJson,
            new string('e', 64));

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(tampered, model);

        _ = Codes(result).Should().Contain("specification_hash_mismatch");
    }

    [Fact]
    public void Compile_WithValidatedModelThatIsNotTheLinkedAsset_Rejects()
    {
        CalibrationSpecification specification = CalibrationGenerationPipeline
            .CompileSpecification(CalibrationMethod.FinalVerification).Value!;
        CalibrationValidatedModel wrongModel = new(
            Guid.NewGuid(),
            new string('f', 64),
            CalibrationModelFormats.Stl,
            "other.stl",
            128,
            "imported",
            1,
            4,
            new CalibrationModelBounds(0m, 0m, 0m, 10m, 10m, 10m),
            "millimeter");

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, wrongModel);

        _ = Codes(result).Should().Contain("plan_model_mismatch");
    }

    [Fact]
    public void Compile_WithUnsupportedDistribution_RejectsBeforeProducingAnyOverride()
    {
        CalibrationGenerationContext baseline = CalibrationGenerationTestData.Context();
        (CalibrationSpecification specification, CalibrationValidatedModel model) = Prepare();
        CalibrationSpecificationDocument tamperedDocument = specification.Document with
        {
            Compatibility = baseline.Compatibility with { SlicerDistribution = "vendor-fork" },
        };
        CalibrationSpecification tampered = new(
            tamperedDocument,
            specification.CanonicalJson,
            specification.Sha256);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(tampered, model);

        _ = Codes(result).Should().Contain("slicer_distribution_unsupported");
        _ = result.Value.Should().BeNull();
    }

    [Fact]
    public void Compile_WithMissingContainerDigest_ReturnsExplicitDependencyError()
    {
        CalibrationGenerationContext baseline = CalibrationGenerationTestData.Context();
        (CalibrationSpecification specification, CalibrationValidatedModel model) = Prepare();
        CalibrationSpecificationDocument tamperedDocument = specification.Document with
        {
            Compatibility = baseline.Compatibility with { SlicerContainerDigest = null },
        };
        CalibrationSpecification tampered = new(
            tamperedDocument,
            specification.CanonicalJson,
            specification.Sha256);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(tampered, model);

        _ = Codes(result).Should().Contain("slicer_container_digest_missing");
        _ = result.Value.Should().BeNull();
    }

    private static CalibrationGenerationContext ContextWithMachineProfile(string machineJson)
    {
        CalibrationGenerationContext baseline = CalibrationGenerationTestData.Context();
        return baseline with
        {
            Profiles = new CalibrationProfileTriplet(
                CalibrationGenerationTestData.Profile(
                    CalibrationGenerationTestData.MachineProfileId,
                    "machine",
                    "PF Machine",
                    machineJson),
                baseline.Profiles.Process,
                baseline.Profiles.Filament),
        };
    }
}
