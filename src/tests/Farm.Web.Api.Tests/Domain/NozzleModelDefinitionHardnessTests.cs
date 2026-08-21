using Farm.Infrastructure.Domain;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Domain;

/// <summary>
/// Covers how <see cref="NozzleModelDefinition.IsHardened"/> resolves, since
/// <c>DispatchScorer</c> uses it as a hard gate for abrasive filaments.
/// </summary>
public sealed class NozzleModelDefinitionHardnessTests
{
    [Theory]
    [InlineData(NozzleType.HardenedSteel)]
    [InlineData(NozzleType.TungstenCarbide)]
    [InlineData(NozzleType.Abrasive)]
    [InlineData(NozzleType.Diamond)]
    [InlineData(NozzleType.Ruby)]
    [InlineData(NozzleType.ToolSteel)]
    public void IsHardened_AbrasionResistantMaterial_DefaultsToHardened(NozzleType nozzleType)
    {
        NozzleModelDefinition nozzle = new() { NozzleType = nozzleType };

        nozzle.HardnessOverride.Should().Be(NozzleHardnessOverride.Auto);
        nozzle.IsHardened.Should().BeTrue();
    }

    [Theory]
    [InlineData(NozzleType.Brass)]
    [InlineData(NozzleType.StainlessSteel)]
    [InlineData(NozzleType.PlatedCopper)]
    [InlineData(NozzleType.Unknown)]
    public void IsHardened_SoftMaterial_DefaultsToNotHardened(NozzleType nozzleType)
    {
        NozzleModelDefinition nozzle = new() { NozzleType = nozzleType };

        nozzle.IsHardened.Should().BeFalse();
    }

    [Fact]
    public void IsHardened_HardenedOverrideOnSoftMaterial_ReturnsTrue()
    {
        NozzleModelDefinition nozzle = new()
        {
            NozzleType = NozzleType.Brass,
            HardnessOverride = NozzleHardnessOverride.Hardened
        };

        nozzle.IsHardened.Should().BeTrue("an explicit override outranks the material default");
    }

    [Fact]
    public void IsHardened_NotHardenedOverrideOnHardMaterial_ReturnsFalse()
    {
        NozzleModelDefinition nozzle = new()
        {
            NozzleType = NozzleType.Diamond,
            HardnessOverride = NozzleHardnessOverride.NotHardened
        };

        nozzle.IsHardened.Should().BeFalse("an explicit override outranks the material default");
    }

    [Fact]
    public void IsHardened_AutoOverride_TracksMaterialChanges()
    {
        NozzleModelDefinition nozzle = new() { NozzleType = NozzleType.Brass };
        nozzle.IsHardened.Should().BeFalse();

        nozzle.NozzleType = NozzleType.Diamond;

        nozzle.IsHardened.Should().BeTrue("Auto keeps hardness following the material");
    }

    [Fact]
    public void NewNozzle_DefaultsToBrassAndAuto()
    {
        NozzleModelDefinition nozzle = new();

        nozzle.NozzleType.Should().Be(NozzleType.Brass);
        nozzle.HardnessOverride.Should().Be(NozzleHardnessOverride.Auto);
        nozzle.IsHardened.Should().BeFalse();
    }
}
