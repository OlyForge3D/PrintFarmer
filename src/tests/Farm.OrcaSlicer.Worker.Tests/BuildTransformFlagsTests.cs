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
        // Z-axis rotation (around up axis) → OrcaSlicer --rotate (yaw).
        // +180° and -180° are the same rotation; this input is a hair over π, so the 'ZYX'
        // re-parameterisation lands on -180. Assert the orientation, not the sign.
        string json = """{"rotation":[0,0,3.1415927],"scale":[1,1,1]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        (double rx, double ry, double rz) = ParseRotationDegrees(result.Flags);
        rx.Should().BeApproximately(0, 1e-2);
        ry.Should().BeApproximately(0, 1e-2);
        Math.Abs(rz).Should().BeApproximately(180, 1e-2);
    }

    [Fact]
    public void BuildTransformFlags_CombinedRotation_ReParameterisedNotVerbatim()
    {
        // 45° on each workspace axis. Emitting 45/45/45 verbatim would be wrong: OrcaSlicer
        // rebuilds the triple as Rz·Ry·Rx, so the equivalent 'ZYX' triple is ~59.64/-8.42/59.64.
        // The orientation itself is verified in
        // BuildTransformFlags_MultiAxisRotation_AnglesReconstructViewerOrientation.
        string json = """{"rotation":[0.7853982,0.7853982,0.7853982],"scale":[1,1,1]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().Contain("--rotate-x");
        result.Flags.Should().Contain("--rotate-y");
        result.Flags.Should().Contain("--rotate ");
        result.Flags.Should().NotContain("45.00", "the workspace angles must not be passed through verbatim");
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
    /// The emitted angles must reconstruct the orientation the user approved in the viewer.
    /// <para>
    /// OrcaSlicer sums <c>--rotate*</c> into an Euler triple (<c>ModelVolume::rotate</c> does
    /// <c>get_rotation() + extract_euler_angles(...)</c>) and rebuilds it as <c>Rz·Ry·Rx</c>
    /// (<c>Geometry::rotation_transform</c>), so flag order is irrelevant and the workspace's
    /// three.js <c>'XYZ'</c> angles cannot be passed through verbatim. This applies
    /// <c>Rz·Ry·Rx</c> to whatever we emitted and compares against hard literals derived
    /// independently from three.js, so it fails if the re-parameterisation is dropped.
    /// </para>
    /// </summary>
    [Fact]
    public void BuildTransformFlags_MultiAxisRotation_AnglesReconstructViewerOrientation()
    {
        // three.js Euler(π/4, π/4, π/4), order 'XYZ' → e1 maps to (0.5, 0.85355339, 0.14644661).
        string json = """{"rotation":[0.7853981633974483,0.7853981633974483,0.7853981633974483],"scale":[1,1,1],"position":[0,0,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        (double rxDeg, double ryDeg, double rzDeg) = ParseRotationDegrees(result.Flags);

        // Passing the workspace angles through unchanged would emit 45/45/45 and be wrong.
        ryDeg.Should().NotBeApproximately(45, 0.5, "the triple must be re-parameterised to 'ZYX'");

        // Tolerance is 1e-3, not 1e-6: the flags are formatted to 2 decimal places, so a
        // reconstruction from them carries up to ~1e-4 of rounding. The 'XYZ' vs 'ZYX'
        // divergence this guards against is O(0.1) — three orders of magnitude larger.
        (double x, double y, double z) = ApplyOrcaZyx(rxDeg, ryDeg, rzDeg, 1, 0, 0);
        x.Should().BeApproximately(0.5, 1e-3);
        y.Should().BeApproximately(0.85355339, 1e-3);
        z.Should().BeApproximately(0.14644661, 1e-3);

        (double x2, double y2, double z2) = ApplyOrcaZyx(rxDeg, ryDeg, rzDeg, 0, 0, 1);
        x2.Should().BeApproximately(0.70710678, 1e-3);
        y2.Should().BeApproximately(-0.5, 1e-3);
        z2.Should().BeApproximately(0.5, 1e-3);
    }

    /// <summary>
    /// Gimbal-lock branch: three.js Euler(π/2, 0, π/2) re-parameterises to a 'ZYX' triple with
    /// |ry'| = 90°, where only the X/Z sum is observable. Viewer oracle: e1 → (0,0,1).
    /// <para>
    /// The canonical triple is also pinned. At lock, <c>r00/r10/r21/r22</c> all collapse to the
    /// residue of a catastrophic cancellation (~6e-17), so deriving X and Z from
    /// <c>atan2</c> of those is numerically meaningless — it happens to yield an equivalent
    /// rotation, (45,-90,45) instead of (90,-90,0), but only by luck. The dedicated branch reads
    /// <c>r01</c>/<c>r02</c>, which are O(1). Asserting Z == 0 is what makes that branch
    /// detectable; without it, deleting the branch passes every other test.
    /// </para>
    /// </summary>
    [Fact]
    public void BuildTransformFlags_GimbalLockedRotation_StillReconstructsViewerOrientation()
    {
        string json = """{"rotation":[1.5707963267948966,0,1.5707963267948966],"scale":[1,1,1],"position":[0,0,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        (double rxDeg, double ryDeg, double rzDeg) = ParseRotationDegrees(result.Flags);

        rxDeg.Should().BeApproximately(90, 1e-2);
        ryDeg.Should().BeApproximately(-90, 1e-2);
        rzDeg.Should().BeApproximately(0, 1e-2, "the locked branch pins Z and solves for X");

        (double x, double y, double z) = ApplyOrcaZyx(rxDeg, ryDeg, rzDeg, 1, 0, 0);
        x.Should().BeApproximately(0, 1e-3);
        y.Should().BeApproximately(0, 1e-3);
        z.Should().BeApproximately(1, 1e-3);

        (double x2, double y2, double z2) = ApplyOrcaZyx(rxDeg, ryDeg, rzDeg, 0, 1, 0);
        x2.Should().BeApproximately(-1, 1e-3);
        y2.Should().BeApproximately(0, 1e-3);
        z2.Should().BeApproximately(0, 1e-3);
    }

    /// <summary>
    /// Single-axis rotations are already a valid 'ZYX' triple, so they must pass through
    /// unchanged — the re-parameterisation must not disturb the common case.
    /// </summary>
    [Theory]
    [InlineData(1.5707963267948966, 0, 0, "--rotate-x 90.00")]
    [InlineData(0, 0.7853981633974483, 0, "--rotate-y 45.00")]
    [InlineData(0, 0, 1.0471975511965976, "--rotate 60.00")]
    public void BuildTransformFlags_SingleAxisRotation_PassesThroughUnchanged(
        double rx, double ry, double rz, string expected)
    {
        string json = $$"""{"rotation":[{{rx.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}},{{ry.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}},{{rz.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}],"scale":[1,1,1],"position":[0,0,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Trim().Should().Be(expected);
    }

    /// <summary>Read the emitted rotation flags back out, in degrees. Missing flag ⇒ 0.</summary>
    private static (double X, double Y, double Z) ParseRotationDegrees(string flags)
    {
        return (Read("--rotate-x"), Read("--rotate-y"), Read("--rotate"));

        double Read(string flag)
        {
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(flags, @"--rotate(-x|-y)? (-?\d+\.\d+)"))
            {
                string name = "--rotate" + match.Groups[1].Value;
                if (name == flag)
                {
                    return double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            return 0;
        }
    }

    /// <summary>
    /// Independent reimplementation of how OrcaSlicer turns its Euler triple into an orientation:
    /// column-vector <c>Rz·Ry·Rx</c> (<c>Geometry::rotation_transform</c>).
    /// </summary>
    private static (double X, double Y, double Z) ApplyOrcaZyx(
        double rxDeg, double ryDeg, double rzDeg, double x, double y, double z)
    {
        double rx = rxDeg * Math.PI / 180.0;
        double ry = ryDeg * Math.PI / 180.0;
        double rz = rzDeg * Math.PI / 180.0;

        // Rx
        double y1 = (y * Math.Cos(rx)) - (z * Math.Sin(rx));
        double z1 = (y * Math.Sin(rx)) + (z * Math.Cos(rx));
        double x1 = x;

        // Ry
        double x2 = (x1 * Math.Cos(ry)) + (z1 * Math.Sin(ry));
        double z2 = (-x1 * Math.Sin(ry)) + (z1 * Math.Cos(ry));
        double y2 = y1;

        // Rz
        double x3 = (x2 * Math.Cos(rz)) - (y2 * Math.Sin(rz));
        double y3 = (x2 * Math.Sin(rz)) + (y2 * Math.Cos(rz));

        return (x3, y3, z2);
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
