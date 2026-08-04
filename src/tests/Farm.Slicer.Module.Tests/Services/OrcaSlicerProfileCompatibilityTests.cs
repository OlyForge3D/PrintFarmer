using Farm.Slicer.Module.Services;
using FluentAssertions;

namespace Farm.Slicer.Module.Tests.Services;

public sealed class OrcaSlicerProfileCompatibilityTests
{
    [Theory]
    [InlineData("2.3.1")]
    [InlineData("2.4.0")]
    [InlineData("2.4.1")]
    [InlineData("2.4.2")]
    [InlineData("2.4.2+8500fcd")]
    public void IsSupportedVersion_WithSupportedProfileVersion_ReturnsTrue(string version)
    {
        bool supported = OrcaSlicerProfileCompatibility.IsSupportedVersion(version);

        _ = supported.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2.3.2")]
    [InlineData("2.5.0")]
    [InlineData("v2.4.2")]
    public void IsSupportedVersion_WithUnknownProfileVersion_ReturnsFalse(string? version)
    {
        bool supported = OrcaSlicerProfileCompatibility.IsSupportedVersion(version);

        _ = supported.Should().BeFalse();
    }
}
