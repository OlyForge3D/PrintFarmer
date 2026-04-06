
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
}
