using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Xunit;
using static Farm.OrcaSlicer.Worker.Services.OrcaSlicingPipelineService;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Tests for <see cref="OrcaSlicingPipelineService.BuildTransformFlags"/>.
/// Verifies Y-up (Three.js) → Z-up (OrcaSlicer) coordinate mapping for
/// rotation, scale, and position transforms.
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
    public void BuildTransformFlags_YRotation_MapsToRotateZ()
    {
        // R3F Y-axis (up) → OrcaSlicer Z-axis → --rotate flag
        string json = """{"rotation":[0,0.7853982,0],"scale":[1,1,1]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--rotate 45.00");
        result.Flags.Should().NotContain("--rotate-x");
        result.Flags.Should().NotContain("--rotate-y");
    }

    [Fact]
    public void BuildTransformFlags_ZRotation_MapsToRotateY()
    {
        // R3F Z-axis → OrcaSlicer Y-axis → --rotate-y flag
        string json = """{"rotation":[0,0,3.1415927],"scale":[1,1,1]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--rotate-y 180.00");
    }

    [Fact]
    public void BuildTransformFlags_CombinedRotation_AllAxesPresent()
    {
        // 45° on each R3F axis
        string json = """{"rotation":[0.7853982,0.7853982,0.7853982],"scale":[1,1,1]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--rotate-x 45.00");
        result.Flags.Should().Contain("--rotate 45.00");
        result.Flags.Should().Contain("--rotate-y 45.00");
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
    public void BuildTransformFlags_PositionOffset_EmitsCenterFlag()
    {
        // position [10, 0, 20] → bed offset X=10, Y=20 (R3F Z → Orca Y)
        string json = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[10,0,20]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--center 10.00,20.00");
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
    public void BuildTransformFlags_NegativePosition_EmitsNegativeCenter()
    {
        string json = """{"position":[-15.5,0,-30.2]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--center -15.50,-30.20");
        result.HasCustomPosition.Should().BeTrue();
    }

    [Fact]
    public void BuildTransformFlags_FullTransform_AllFlagsPresent()
    {
        // 90° X rotation, 2x scale, position offset
        string json = """{"rotation":[1.5707963,0,0],"scale":[2,2,2],"position":[50,0,75]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--rotate-x 90.00");
        result.Flags.Should().Contain("--scale 2.0000");
        result.Flags.Should().Contain("--center 50.00,75.00");
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
    public void BuildTransformFlags_OnlyXPosition_EmitsCenterWithZeroY()
    {
        string json = """{"position":[25,0,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--center 25.00,0.00");
        result.HasCustomPosition.Should().BeTrue();
    }

    [Fact]
    public void BuildTransformFlags_YPositionIgnored_OnlyBedPlaneMatters()
    {
        // Y is the vertical axis in Three.js — vertical offset does not affect bed placement
        string json = """{"position":[0,100,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().NotContain("--center");
        result.HasCustomPosition.Should().BeFalse();
    }
}
