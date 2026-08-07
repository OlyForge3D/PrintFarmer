using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Calibration.Generation;

/// <summary>Covers the bounded calibration slicer compatibility policy.</summary>
public sealed class CalibrationSlicerCompatibilityPolicyTests
{
    [Fact]
    public void Constructor_WithoutConfiguredVersions_UsesCurrentDefault()
    {
        CalibrationSlicerCompatibilityPolicy policy = new(null);

        _ = policy.SupportedVersions.Should()
            .Equal(CalibrationContractConstants.SlicerVersion);
    }

    [Fact]
    public void IsSupported_WithConfiguredVersionAndBuildMetadata_ReturnsTrue()
    {
        CalibrationSlicerCompatibilityPolicy policy = new(["2.4.2", "2.5.0"]);

        _ = policy.IsSupported("2.5.0").Should().BeTrue();
        _ = policy.IsSupported("2.5.0+worker.17").Should().BeTrue();
        _ = policy.IsSupported("2.6.0").Should().BeFalse();
    }

    [Fact]
    public void TupleValidator_WithConfiguredNonDefaultVersion_IsSupported()
    {
        CalibrationSlicerCompatibilityPolicy policy = new(["2.5.0"]);
        CalibrationCompatibilityIdentity identity = new(
            CalibrationSupportedTuple.FirmwareFamily,
            CalibrationSupportedTuple.GcodeDialect,
            CalibrationSupportedTuple.SlicerEngine,
            CalibrationSupportedTuple.SlicerDistribution,
            "2.5.0",
            CalibrationGenerationHarness.ContainerDigest,
            CalibrationGenerationHarness.BinaryDigest,
            CalibrationSupportedTuple.ProfileFormat);

        bool supported = CalibrationSupportedTupleValidator.IsSupported(identity, policy);

        _ = supported.Should().BeTrue();
    }

    [Fact]
    public void Constructor_WithMoreThanBoundedMaximum_Throws()
    {
        string[] versions = Enumerable.Range(0, 33)
            .Select(patch => $"2.4.{patch}")
            .ToArray();

        Action create = () => _ = new CalibrationSlicerCompatibilityPolicy(versions);

        _ = create.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("2.4")]
    [InlineData("2.4.2-preview")]
    [InlineData("latest")]
    public void Constructor_WithInvalidVersion_Throws(string version)
    {
        Action create = () => _ = new CalibrationSlicerCompatibilityPolicy([version]);

        _ = create.Should().Throw<ArgumentException>();
    }
}
