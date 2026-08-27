using Farm.Infrastructure.Domain;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Domain;

/// <summary>
/// Covers how <see cref="NozzleModelDefinition.IsHardened"/> resolves, since
/// <c>DispatchScorer</c> uses it as a hard gate for abrasive filaments.
/// </summary>
public sealed class NozzleModelDefinitionHardnessTests
{
    private static NozzleMaterial Material(NozzleType nozzleType) => new()
    {
        Id = Guid.NewGuid(),
        Name = nozzleType.ToString(),
        IsHardened = nozzleType is NozzleType.HardenedSteel or NozzleType.TungstenCarbide
            or NozzleType.Abrasive or NozzleType.Diamond or NozzleType.Ruby or NozzleType.ToolSteel,
        DefaultMaxTemp = 500,
        IsBuiltIn = true
    };

    [Theory]
    [InlineData(NozzleType.HardenedSteel)]
    [InlineData(NozzleType.TungstenCarbide)]
    [InlineData(NozzleType.Abrasive)]
    [InlineData(NozzleType.Diamond)]
    [InlineData(NozzleType.Ruby)]
    [InlineData(NozzleType.ToolSteel)]
    public void IsHardened_AbrasionResistantMaterial_DefaultsToHardened(NozzleType nozzleType)
    {
        NozzleModelDefinition nozzle = new() { NozzleMaterial = Material(nozzleType) };

        nozzle.HardnessOverride.Should().Be(NozzleHardnessOverride.Auto);
        nozzle.IsHardened.Should().BeTrue();
    }

    [Theory]
    [InlineData(NozzleType.Brass)]
    [InlineData(NozzleType.StainlessSteel)]
    [InlineData(NozzleType.PlatedCopper)]
    public void IsHardened_SoftMaterial_DefaultsToNotHardened(NozzleType nozzleType)
    {
        NozzleModelDefinition nozzle = new() { NozzleMaterial = Material(nozzleType) };

        nozzle.IsHardened.Should().BeFalse();
    }

    [Fact]
    public void IsHardened_NoMaterialResolved_DefaultsToNotHardened()
    {
        NozzleModelDefinition nozzle = new();

        nozzle.NozzleType.Should().Be(NozzleType.Unknown);
        nozzle.IsHardened.Should().BeFalse();
    }

    [Fact]
    public void IsHardened_HardenedOverrideOnSoftMaterial_ReturnsTrue()
    {
        NozzleModelDefinition nozzle = new()
        {
            NozzleMaterial = Material(NozzleType.Brass),
            HardnessOverride = NozzleHardnessOverride.Hardened
        };

        nozzle.IsHardened.Should().BeTrue("an explicit override outranks the material default");
    }

    [Fact]
    public void IsHardened_NotHardenedOverrideOnHardMaterial_ReturnsFalse()
    {
        NozzleModelDefinition nozzle = new()
        {
            NozzleMaterial = Material(NozzleType.Diamond),
            HardnessOverride = NozzleHardnessOverride.NotHardened
        };

        nozzle.IsHardened.Should().BeFalse("an explicit override outranks the material default");
    }

    [Fact]
    public void IsHardened_AutoOverride_TracksMaterialChanges()
    {
        NozzleModelDefinition nozzle = new() { NozzleMaterial = Material(NozzleType.Brass) };
        nozzle.IsHardened.Should().BeFalse();

        nozzle.NozzleMaterial = Material(NozzleType.Diamond);

        nozzle.IsHardened.Should().BeTrue("Auto keeps hardness following the material");
    }

    [Fact]
    public void NewNozzle_DefaultsToAutoAndNotHardened()
    {
        NozzleModelDefinition nozzle = new();

        nozzle.HardnessOverride.Should().Be(NozzleHardnessOverride.Auto);
        nozzle.IsHardened.Should().BeFalse();
    }
}
