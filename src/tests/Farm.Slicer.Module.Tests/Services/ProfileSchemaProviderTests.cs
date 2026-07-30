
using FluentAssertions;

namespace Farm.Slicer.Module.Tests.Services;

public class ProfileSchemaProviderTests
{
    [Fact]
    public void GetAllSchemas_ReturnsThreeProfileTypes()
    {
        ProfileSchemasResponseDto result = ProfileSchemaProvider.GetAllSchemas();

        result.Process.Should().NotBeNull();
        result.Machine.Should().NotBeNull();
        result.Filament.Should().NotBeNull();
        result.Process.ProfileType.Should().Be("process");
        result.Machine.ProfileType.Should().Be("machine");
        result.Filament.ProfileType.Should().Be("filament");
    }

    [Fact]
    public void GetProcessSchema_HasExpectedCategories()
    {
        ProfileTypeSchemaDto schema = ProfileSchemaProvider.GetProcessSchema();

        schema.Categories.Should().BeEquivalentTo(
            ["quality", "strength", "speed", "support", "adhesion", "temperature", "other"],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void GetProcessSchema_ContainsLayerHeightField()
    {
        ProfileTypeSchemaDto schema = ProfileSchemaProvider.GetProcessSchema();

        ProfileFieldMetadata? field = schema.Fields.SingleOrDefault(f => f.Key == "layerHeight");
        field.Should().NotBeNull();
        field!.FieldType.Should().Be("number");
        field.Unit.Should().Be("mm");
        field.Min.Should().BeGreaterThan(0);
        field.Max.Should().BeGreaterThanOrEqualTo(field.Min!.Value);
    }

    [Fact]
    public void GetProcessSchema_ContainsInfillPatternField()
    {
        ProfileTypeSchemaDto schema = ProfileSchemaProvider.GetProcessSchema();

        ProfileFieldMetadata? field = schema.Fields.SingleOrDefault(f => f.Key == "infillPattern");
        field.Should().NotBeNull();
        field!.FieldType.Should().Be("enum");
        field.Options.Should().NotBeNullOrEmpty();

        List<string> values = field.Options!.Select(o => o.Value).ToList();
        values.Should().Contain("grid");
        values.Should().Contain("gyroid");
        values.Should().Contain("lightning");
    }

    [Fact]
    public void GetMachineSchema_HasBuildVolumeFields()
    {
        ProfileTypeSchemaDto schema = ProfileSchemaProvider.GetMachineSchema();

        foreach (string key in new[] { "buildVolumeX", "buildVolumeY", "buildVolumeZ" })
        {
            ProfileFieldMetadata? field = schema.Fields.SingleOrDefault(f => f.Key == key);
            field.Should().NotBeNull($"field '{key}' should exist");
            field!.Unit.Should().Be("mm");
        }
    }

    [Fact]
    public void GetMachineSchema_HasMotionTypeEnum()
    {
        ProfileTypeSchemaDto schema = ProfileSchemaProvider.GetMachineSchema();

        ProfileFieldMetadata? field = schema.Fields.SingleOrDefault(f => f.Key == "motionType");
        field.Should().NotBeNull();
        field!.FieldType.Should().Be("enum");
        field.Options.Should().NotBeNullOrEmpty();

        List<string> values = field.Options!.Select(o => o.Value).ToList();
        values.Should().Contain("cartesian");
        values.Should().Contain("corexy");
        values.Should().Contain("delta");
    }

    [Fact]
    public void GetFilamentSchema_HasTemperatureFields()
    {
        ProfileTypeSchemaDto schema = ProfileSchemaProvider.GetFilamentSchema();

        foreach (string key in new[] { "nozzleTemperature", "bedTemperature" })
        {
            ProfileFieldMetadata? field = schema.Fields.SingleOrDefault(f => f.Key == key);
            field.Should().NotBeNull($"field '{key}' should exist");
            field!.Unit.Should().Be("°C");
        }
    }

    [Fact]
    public void GetFilamentSchema_HasCoolingFields()
    {
        ProfileTypeSchemaDto schema = ProfileSchemaProvider.GetFilamentSchema();

        foreach (string key in new[] { "minFanSpeed", "maxFanSpeed" })
        {
            ProfileFieldMetadata? field = schema.Fields.SingleOrDefault(f => f.Key == key);
            field.Should().NotBeNull($"field '{key}' should exist");
            field!.Unit.Should().Be("%");
            field.Min.Should().Be(0);
            field.Max.Should().Be(100);
        }
    }

    [Fact]
    public void AllSchemas_FieldKeysAreUnique()
    {
        ProfileSchemasResponseDto all = ProfileSchemaProvider.GetAllSchemas();

        foreach (ProfileTypeSchemaDto schema in new[] { all.Process, all.Machine, all.Filament })
        {
            List<string> keys = schema.Fields.Select(f => f.Key).ToList();
            keys.Should().OnlyHaveUniqueItems($"schema '{schema.ProfileType}' must not have duplicate keys");
        }
    }

    [Fact]
    public void AllSchemas_AllFieldsHaveLabels()
    {
        ProfileSchemasResponseDto all = ProfileSchemaProvider.GetAllSchemas();

        foreach (ProfileTypeSchemaDto schema in new[] { all.Process, all.Machine, all.Filament })
        {
            foreach (ProfileFieldMetadata field in schema.Fields)
            {
                field.Label.Should().NotBeNullOrWhiteSpace(
                    $"field '{field.Key}' in schema '{schema.ProfileType}' must have a label");
            }
        }
    }

    // ── Engine-version filtering (issue #578) ────────────────────────

    [Fact]
    public void GetProcessSchema_WithNullEngineVersion_ReturnsAllFieldsIncludingVersioned()
    {
        ProfileTypeSchemaDto schema = ProfileSchemaProvider.GetProcessSchema(engineVersion: null);
        List<string> keys = schema.Fields.Select(f => f.Key).ToList();

        keys.Should().Contain("wallGenerator", "added-in-2.4 field must be present without version filter");
        keys.Should().Contain("enableArcFitting", "added-in-2.4 field must be present without version filter");
        keys.Should().Contain("legacyPreviewSetting", "removed-in-2.4 field must be present without version filter");
        keys.Should().Contain("bedAdhesionOverride", "renamed field must use post-rename key without version filter");
        keys.Should().NotContain("firstLayerAdhesion", "pre-rename key must not appear without version filter");
    }

    [Theory]
    [InlineData("2.3.1")]
    [InlineData("2.3.0")]
    public void GetProcessSchema_WithLegacyVersion_DropsAddedFieldsAndEmitsRenamedOldKey(string engineVersion)
    {
        ProfileTypeSchemaDto schema = ProfileSchemaProvider.GetProcessSchema(engineVersion);
        List<string> keys = schema.Fields.Select(f => f.Key).ToList();

        keys.Should().NotContain("wallGenerator",
            $"added-in-2.4 fields must be absent for engine {engineVersion}");
        keys.Should().NotContain("enableArcFitting",
            $"added-in-2.4 fields must be absent for engine {engineVersion}");
        keys.Should().Contain("legacyPreviewSetting",
            $"pre-2.4 fields must still appear for engine {engineVersion}");
        keys.Should().Contain("firstLayerAdhesion",
            $"renamed field must appear under its pre-2.4 key for engine {engineVersion}");
        keys.Should().NotContain("bedAdhesionOverride",
            $"post-rename key must not leak into engine {engineVersion} payload");
    }

    [Theory]
    [InlineData("2.4.0")]
    [InlineData("2.4.1")]
    [InlineData("2.5.0")]
    public void GetProcessSchema_WithCurrentVersion_AddsNewFieldsAndDropsRetiredOnes(string engineVersion)
    {
        ProfileTypeSchemaDto schema = ProfileSchemaProvider.GetProcessSchema(engineVersion);
        List<string> keys = schema.Fields.Select(f => f.Key).ToList();

        keys.Should().Contain("wallGenerator",
            $"added-in-2.4 fields must be present for engine {engineVersion}");
        keys.Should().Contain("enableArcFitting",
            $"added-in-2.4 fields must be present for engine {engineVersion}");
        keys.Should().NotContain("legacyPreviewSetting",
            $"retired-in-2.4 field must be absent for engine {engineVersion}");
        keys.Should().Contain("bedAdhesionOverride",
            $"renamed field must appear under its post-2.4 key for engine {engineVersion}");
        keys.Should().NotContain("firstLayerAdhesion",
            $"pre-rename key must not leak into engine {engineVersion} payload");
    }

    [Fact]
    public void GetProcessSchema_UnparsableEngineVersion_ReturnsAllFieldsUnfiltered()
    {
        // Malformed / non-System.Version strings must not crash; fall back to unfiltered.
        ProfileTypeSchemaDto schema = ProfileSchemaProvider.GetProcessSchema("nightly-2.4-preview");
        List<string> keys = schema.Fields.Select(f => f.Key).ToList();

        keys.Should().Contain("wallGenerator");
        keys.Should().Contain("legacyPreviewSetting");
        keys.Should().Contain("bedAdhesionOverride");
    }

    [Fact]
    public void GetAllSchemas_VersionParameterFlowsToProcess()
    {
        ProfileSchemasResponseDto legacy = ProfileSchemaProvider.GetAllSchemas("2.3.1");
        ProfileSchemasResponseDto current = ProfileSchemaProvider.GetAllSchemas("2.4.1");

        legacy.Process.Fields.Should().NotContain(f => f.Key == "wallGenerator");
        current.Process.Fields.Should().Contain(f => f.Key == "wallGenerator");
    }

    [Fact]
    public void GetProcessSchema_RenamedField_PreservesMetadataExceptKey()
    {
        // A field renamed via RenamedFromKey should preserve everything except Key when
        // emitted under the old key — so consumers get identical labels/enum options/etc.
        ProfileTypeSchemaDto legacy = ProfileSchemaProvider.GetProcessSchema("2.3.1");
        ProfileTypeSchemaDto current = ProfileSchemaProvider.GetProcessSchema("2.4.1");

        ProfileFieldMetadata legacyField = legacy.Fields.Single(f => f.Key == "firstLayerAdhesion");
        ProfileFieldMetadata currentField = current.Fields.Single(f => f.Key == "bedAdhesionOverride");

        legacyField.FieldType.Should().Be(currentField.FieldType);
        legacyField.Category.Should().Be(currentField.Category);
        legacyField.Options.Should().BeEquivalentTo(currentField.Options);
        legacyField.DefaultValue.Should().Be(currentField.DefaultValue);
    }

    [Fact]
    public void ApplyEngineVersion_EmptyList_ReturnsEmpty()
    {
        List<ProfileFieldMetadata> empty = [];
        List<ProfileFieldMetadata> result = ProfileSchemaProvider.ApplyEngineVersion(empty, "2.3.1");
        result.Should().BeEmpty();
    }
}
