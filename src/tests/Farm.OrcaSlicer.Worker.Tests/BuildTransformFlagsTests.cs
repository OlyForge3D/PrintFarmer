using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Xunit;
using static Farm.OrcaSlicer.Worker.Services.OrcaSlicingPipelineService;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Tests for <see cref="OrcaSlicingPipelineService.BuildTransformFlags"/>.
/// Workspace is Z-up with XY bed plane (camera.up = [0,0,1]).
/// Axes map directly: X→X, Y→Y, Z→Z between workspace and OrcaSlicer.
/// </summary>
public class BuildTransformFlagsTests
{
    [Fact]
    public void BuildTransformFlags_NullInput_ReturnsEmptyNoPosition()
    {
        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(null);

        result.Flags.Should().BeEmpty();
        result.HasCustomPosition.Should().BeFalse();
    }

    [Fact]
    public void BuildTransformFlags_EmptyString_ReturnsEmptyNoPosition()
    {
        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags("  ");

        result.Flags.Should().BeEmpty();
        result.HasCustomPosition.Should().BeFalse();
    }

    [Fact]
    public void BuildTransformFlags_InvalidJson_ReturnsEmptyNoPosition()
    {
        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags("{not valid}");

        result.Flags.Should().BeEmpty();
        result.HasCustomPosition.Should().BeFalse();
    }

    [Fact]
    public void BuildTransformFlags_IdentityTransform_ReturnsEmptyFlags()
    {
        string json = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[0,0,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().BeEmpty();
        result.HasCustomPosition.Should().BeFalse();
    }

    [Fact]
    public void BuildTransformFlags_XRotation90Deg_MapsToRotateX()
    {
        // π/2 radians ≈ 90 degrees around R3F X-axis
        string json = """{"rotation":[1.5707963,0,0],"scale":[1,1,1]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--rotate-x 90.00");
        result.HasCustomPosition.Should().BeFalse();
    }

    [Fact]
    public void BuildTransformFlags_YRotation_MapsToRotateY()
    {
        // Y-axis rotation → OrcaSlicer --rotate-y
        string json = """{"rotation":[0,0.7853982,0],"scale":[1,1,1]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--rotate-y 45.00");
        result.Flags.Should().NotContain("--rotate-x");
        result.Flags.Should().NotContain("--rotate ");
    }

    [Fact]
    public void BuildTransformFlags_ZRotation_MapsToRotate()
    {
        // Z-axis rotation (around up axis) → OrcaSlicer --rotate (yaw)
        string json = """{"rotation":[0,0,3.1415927],"scale":[1,1,1]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--rotate 180.00");
    }

    [Fact]
    public void BuildTransformFlags_CombinedRotation_AllAxesPresent()
    {
        // 45° on each R3F axis
        string json = """{"rotation":[0.7853982,0.7853982,0.7853982],"scale":[1,1,1]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--rotate-x 45.00");
        result.Flags.Should().Contain("--rotate-y 45.00");
        result.Flags.Should().Contain("--rotate 45.00");
    }

    [Fact]
    public void BuildTransformFlags_ScaleHalf_EmitsScaleFlag()
    {
        string json = """{"rotation":[0,0,0],"scale":[0.5,0.5,0.5]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--scale 0.5000");
    }

    [Fact]
    public void BuildTransformFlags_ScaleDouble_EmitsScaleFlag()
    {
        string json = """{"rotation":[0,0,0],"scale":[2,2,2]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--scale 2.0000");
    }

    [Fact]
    public void BuildTransformFlags_UniformScale1_NoScaleFlag()
    {
        string json = """{"scale":[1,1,1]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().NotContain("--scale");
    }

    [Fact]
    public void BuildTransformFlags_PositionOffset_ReportsPositionWithoutCenterFlag()
    {
        // position [10, 20, 0] → bed offset X=10, Y=20 (Z-up: XY is bed plane)
        string json = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[10,20,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        // OrcaSlicer 2.4.2 has no --center option: emitting one aborts the run with
        // CLI_INVALID_PARAMS before slicing (#1794). Placement is embedded in a 3MF instead.
        result.Flags.Should().NotContain("--center");
        result.Flags.Should().BeEmpty();
        result.HasCustomPosition.Should().BeTrue();
    }

    [Fact]
    public void BuildTransformFlags_PositionOrigin_NoCenterFlag()
    {
        string json = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[0,0,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().NotContain("--center");
        result.HasCustomPosition.Should().BeFalse();
    }

    [Fact]
    public void BuildTransformFlags_NegativePosition_ReportsPositionWithoutCenterFlag()
    {
        string json = """{"position":[-15.5,-30.2,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().NotContain("--center");
        result.HasCustomPosition.Should().BeTrue();
    }

    [Fact]
    public void BuildTransformFlags_FullTransform_RotationAndScaleOnly()
    {
        // 90° X rotation, 2x scale, position offset on XY bed plane
        string json = """{"rotation":[1.5707963,0,0],"scale":[2,2,2],"position":[50,75,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--rotate-x 90.00");
        result.Flags.Should().Contain("--scale 2.0000");
        result.Flags.Should().NotContain("--center");
        result.HasCustomPosition.Should().BeTrue();
    }

    [Fact]
    public void BuildTransformFlags_MissingPosition_NoPositionFlag()
    {
        // Legacy format without position field
        string json = """{"rotation":[0,0,0],"scale":[1,1,1]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().NotContain("--center");
        result.HasCustomPosition.Should().BeFalse();
    }

    [Fact]
    public void BuildTransformFlags_OnlyXPosition_ReportsPositionWithoutCenterFlag()
    {
        string json = """{"position":[25,0,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().NotContain("--center");
        result.HasCustomPosition.Should().BeTrue();
    }

    [Fact]
    public void BuildTransformFlags_ZPositionIgnored_OnlyBedPlaneMatters()
    {
        // Z is the vertical axis (up) — vertical offset does not affect bed placement
        string json = """{"position":[0,0,100]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().NotContain("--center");
        result.HasCustomPosition.Should().BeFalse();
    }

    [Fact]
    public void BuildTransformFlags_NaNValues_TreatedAsZero()
    {
        // JSON.stringify(NaN) → null — non-numeric elements should be treated as 0
        string json = """{"rotation":[null,0,0],"scale":[null,1,1],"position":[null,null,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().BeEmpty();
        result.HasCustomPosition.Should().BeFalse();
    }

    [Fact]
    public void BuildTransformFlags_OnlyYPosition_ReportsPositionWithoutCenterFlag()
    {
        string json = """{"position":[0,30,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().NotContain("--center");
        result.HasCustomPosition.Should().BeTrue();
    }

    /// <summary>
    /// OrcaSlicer applies <c>--rotate*</c> options in command-line order, each about a world
    /// axis. The workspace viewer uses three.js' default Euler order 'XYZ' (column-vector
    /// Rx·Ry·Rz), which is reproduced by rotating Z first, then Y, then X. Emitting X→Y→Z
    /// instead gives 'ZYX' and mis-orients any multi-axis rotation — invisible to a
    /// single-axis test, so the order is pinned here explicitly.
    /// </summary>
    [Fact]
    public void BuildTransformFlags_MultiAxisRotation_EmitsZThenYThenX()
    {
        string json = """{"rotation":[0.5235987755982988,0.7853981633974483,1.5707963267948966],"scale":[1,1,1],"position":[0,0,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        int z = result.Flags.IndexOf("--rotate 90.00", StringComparison.Ordinal);
        int y = result.Flags.IndexOf("--rotate-y 45.00", StringComparison.Ordinal);
        int x = result.Flags.IndexOf("--rotate-x 30.00", StringComparison.Ordinal);

        z.Should().BeGreaterThanOrEqualTo(0);
        y.Should().BeGreaterThan(z, "Y must be applied after Z");
        x.Should().BeGreaterThan(y, "X must be applied after Y");
    }

    /// <summary>
    /// Regression guard for #1794: <c>--center</c> is not a valid OrcaSlicer 2.4.2 option
    /// (it is commented out of <c>CLITransformConfigDef</c>), so it must never appear in the
    /// flags for ANY transform shape. Passing it aborts the run with exit 254.
    /// </summary>
    [Theory]
    [InlineData("""{"rotation":[0,0,0],"scale":[1,1,1],"position":[30,0,0]}""")]
    [InlineData("""{"rotation":[0,0,0],"scale":[1,1,1],"position":[0,45,0]}""")]
    [InlineData("""{"rotation":[0,0,0],"scale":[1,1,1],"position":[-12.5,88.25,3]}""")]
    [InlineData("""{"rotation":[1.5707963,0.7853981,3.1415926],"scale":[2,2,2],"position":[110,110,0]}""")]
    [InlineData("""{"position":[0.0011,0.0011,0]}""")]
    public void BuildTransformFlags_NeverEmitsUnsupportedPositionalFlags(string json)
    {
        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().NotContain("--center");
        result.Flags.Should().NotContain("--align-xy");
        result.Flags.Should().NotContain("--align_xy");
    }
}
