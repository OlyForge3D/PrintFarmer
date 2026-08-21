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
        // A positive Z-only rotation is already a valid 'ZYX' triple and passes straight through.
        string json = """{"rotation":[0,0,1.5707963267948966],"scale":[1,1,1]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Trim().Should().Be("--rotate 90.00");
    }

    /// <summary>
    /// A 180° Z rotation is re-expressed as X+Y rather than a negative <c>--rotate</c>, because
    /// this input is a hair over π so the extraction lands just below zero and the negative-Z
    /// correction fires. <c>Rz(π) == Ry(π)·Rx(π)</c>, so the orientation is identical — which is
    /// what is asserted, rather than the particular flags.
    /// </summary>
    [Fact]
    public void BuildTransformFlags_ZRotation180_ReExpressedButOrientationPreserved()
    {
        string json = """{"rotation":[0,0,3.1415927],"scale":[1,1,1]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        result.Flags.Should().NotContain("--rotate -", "a negative Z cannot survive accumulation");
        MaxAbsDifference(ViewerRotation(0, 0, 3.1415927), SimulateOrcaCli(result.Flags))
            .Should().BeLessThan(1e-3);
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
    /// The emitted angles must reconstruct the orientation the user approved in the viewer,
    /// after OrcaSlicer's real processing — which is extract-an-Euler-triple-per-flag, SUM the
    /// triples (<c>ModelVolume::rotate</c>), then compose as <c>Rz·Ry·Rx</c>
    /// (<c>Geometry::rotation_transform</c>). <see cref="SimulateOrcaCli"/> models that whole
    /// chain rather than applying <c>Rz·Ry·Rx</c> to the emitted triple directly, because the
    /// extract-and-accumulate step is where a negative Z goes wrong.
    /// </summary>
    /// <remarks>
    /// The negative-Z rows are the ones that matter: before the second 'ZYX' representative was
    /// used they were off by up to 129°, and no assertion in this suite could see it. The
    /// tolerance is 1e-3 because the flags are F2-formatted; the divergence being guarded
    /// against is O(1) in matrix terms.
    /// </remarks>
    [Theory]
    [InlineData(45.0, 45.0, 45.0)]
    [InlineData(90.0, 0.0, 90.0)]
    [InlineData(22.92, 51.57, -74.48)]
    [InlineData(34.38, -11.46, -63.03)]
    [InlineData(28.65, 0.0, -28.65)]
    [InlineData(-22.92, 17.19, -34.38)]
    [InlineData(0.0, 0.0, -30.0)]
    [InlineData(-120.0, 70.0, -160.0)]
    [InlineData(10.0, -80.0, -5.0)]
    public void BuildTransformFlags_Rotation_SurvivesOrcaSlicerAccumulation(
        double rxDeg, double ryDeg, double rzDeg)
    {
        double rx = rxDeg * Math.PI / 180.0;
        double ry = ryDeg * Math.PI / 180.0;
        double rz = rzDeg * Math.PI / 180.0;
        string json = $$"""{"rotation":[{{Inv(rx)}},{{Inv(ry)}},{{Inv(rz)}}],"scale":[1,1,1],"position":[0,0,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        double[,] expected = ViewerRotation(rx, ry, rz);
        double[,] actual = SimulateOrcaCli(result.Flags);

        MaxAbsDifference(expected, actual).Should().BeLessThan(1e-3);
    }

    /// <summary>
    /// Self-check on <see cref="ExtractEulerAnglesLikeOrca"/>: OrcaSlicer documents that
    /// <c>rotation_transform(extract_euler_angles(M)) == M</c>. If this transcription of Eigen's
    /// <c>eulerAngles(2,1,0)</c> is wrong, the test above would be measuring the wrong thing, so
    /// the helper is validated against OrcaSlicer's own contract before being trusted.
    /// </summary>
    [Fact]
    public void ExtractEulerAnglesLikeOrca_SatisfiesOrcaSlicersRoundTripContract()
    {
        ulong seed = 20260820UL;
        double worst = 0;

        for (int i = 0; i < 2000; i++)
        {
            double[,] m = ViewerRotation(
                NextAngle(ref seed),
                NextAngle(ref seed),
                NextAngle(ref seed));

            (double x, double y, double z) = ExtractEulerAnglesLikeOrca(m);
            worst = Math.Max(worst, MaxAbsDifference(m, RotationTransform(x, y, z)));
        }

        worst.Should().BeLessThan(1e-9);
    }

    /// <summary>
    /// Sweep of realistic auto-orient output. <c>autoOrient.ts</c> builds its rotation from
    /// <c>setFromUnitVectors</c> and converts with a default-order <c>THREE.Euler</c>, so
    /// multi-axis rotations with a negative Z are routine, not exotic — roughly half of this
    /// sweep was mis-oriented before the negative-Z correction.
    /// </summary>
    [Fact]
    public void BuildTransformFlags_RandomRotations_AllSurviveAccumulation()
    {
        ulong seed = 1794UL;
        double worst = 0;
        double worstRx = 0, worstRy = 0, worstRz = 0;

        for (int i = 0; i < 1500; i++)
        {
            double rx = NextAngle(ref seed);
            double ry = NextAngle(ref seed);
            double rz = NextAngle(ref seed);
            string json = $$"""{"rotation":[{{Inv(rx)}},{{Inv(ry)}},{{Inv(rz)}}],"scale":[1,1,1],"position":[0,0,0]}""";

            TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);
            double diff = MaxAbsDifference(ViewerRotation(rx, ry, rz), SimulateOrcaCli(result.Flags));

            if (diff > worst)
            {
                (worst, worstRx, worstRy, worstRz) = (diff, rx, ry, rz);
            }
        }

        worst.Should().BeLessThan(
            1e-3,
            "worst case was rotation [{0}, {1}, {2}]",
            worstRx,
            worstRy,
            worstRz);
    }

    /// <summary>
    /// Deterministic angle sequence in (-π, π]. A hand-rolled 64-bit LCG rather than
    /// <see cref="Random"/> because the repository enforces CA5394 as an error; a fixed seed also
    /// makes any failure exactly reproducible.
    /// </summary>
    private static double NextAngle(ref ulong state)
    {
        state = unchecked((state * 6364136223846793005UL) + 1442695040888963407UL);
        double unit = ((state >> 11) & ((1UL << 53) - 1)) / (double)(1UL << 53);
        return ((unit * 2) - 1) * Math.PI;
    }

    /// <summary>
    /// A negative Z must never be emitted alongside an X or Y rotation.
    /// <c>Geometry::extract_euler_angles</c> normalises its Z component into [0, π] (Eigen
    /// <c>eulerAngles(2,1,0)</c>), so <c>extract(Rz(γ))</c> for <c>γ &lt; 0</c> is
    /// <c>(π, -π, γ+π)</c>, not <c>(0, 0, γ)</c> — and those spurious X/Y terms get SUMMED into
    /// the real ones. This pins the structural property directly, independently of the
    /// orientation assertions above.
    /// </summary>
    [Theory]
    [InlineData(22.92, 51.57, -74.48)]
    [InlineData(34.38, -11.46, -63.03)]
    [InlineData(28.65, 0.0, -28.65)]
    [InlineData(-120.0, 70.0, -160.0)]
    public void BuildTransformFlags_NegativeZ_IsNeverEmittedWithAnXOrYRotation(
        double rxDeg, double ryDeg, double rzDeg)
    {
        string json = $$"""{"rotation":[{{Inv(rxDeg * Math.PI / 180.0)}},{{Inv(ryDeg * Math.PI / 180.0)}},{{Inv(rzDeg * Math.PI / 180.0)}}],"scale":[1,1,1],"position":[0,0,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        (double x, double y, double z) = ParseRotationDegrees(result.Flags);

        if (Math.Abs(x) > 1e-9 || Math.Abs(y) > 1e-9)
        {
            z.Should().BeGreaterThanOrEqualTo(0, "a negative --rotate cannot survive accumulation");
        }
    }

    /// <summary>
    /// Gimbal-lock branch: three.js Euler(π/2, 0, π/2) re-parameterises to a 'ZYX' triple with
    /// |ry'| = 90°, where only the X/Z sum is observable.
    /// <para>
    /// The canonical triple is pinned as well as the orientation. At lock,
    /// <c>r00/r10/r21/r22</c> all collapse to the residue of a catastrophic cancellation
    /// (~6e-17), so deriving X and Z from <c>atan2</c> of those is numerically meaningless — it
    /// happens to yield an equivalent rotation, (45,-90,45) instead of (90,-90,0), but only by
    /// luck. Asserting Z == 0 is what makes the branch detectable; without it, deleting the
    /// branch passes every other test.
    /// </para>
    /// </summary>
    [Fact]
    public void BuildTransformFlags_GimbalLockedRotation_StillReconstructsViewerOrientation()
    {
        double halfPi = Math.PI / 2;
        string json = """{"rotation":[1.5707963267948966,0,1.5707963267948966],"scale":[1,1,1],"position":[0,0,0]}""";

        TransformResult result = OrcaSlicingPipelineService.BuildTransformFlags(json);

        (double rxDeg, double ryDeg, double rzDeg) = ParseRotationDegrees(result.Flags);
        rxDeg.Should().BeApproximately(90, 1e-2);
        ryDeg.Should().BeApproximately(-90, 1e-2);
        rzDeg.Should().BeApproximately(0, 1e-2, "the locked branch pins Z and solves for X");

        MaxAbsDifference(ViewerRotation(halfPi, 0, halfPi), SimulateOrcaCli(result.Flags))
            .Should().BeLessThan(1e-3);
    }

    private static string Inv(double value) =>
        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

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
    /// Faithful model of <c>Geometry::extract_euler_angles</c>: Eigen's
    /// <c>eulerAngles(2,1,0)</c> (Eigen 3.4 <c>EulerAngles.h</c>, transcribed for a0=2, a1=1,
    /// a2=0 ⇒ odd=1, i=2, j=1, k=0) followed by <c>std::swap(angles(0), angles(2))</c>.
    /// <para>
    /// The <c>res0 += π</c> normalisation is the whole point: it forces the Z component into
    /// [0, π], so a negative Z cannot survive as a pure single-axis triple.
    /// </para>
    /// </summary>
    private static (double X, double Y, double Z) ExtractEulerAnglesLikeOrca(double[,] m)
    {
        double res0 = Math.Atan2(m[1, 0], m[0, 0]);
        double c2 = Math.Sqrt((m[2, 2] * m[2, 2]) + (m[2, 1] * m[2, 1]));

        double res1;
        if (res0 < 0)
        {
            res0 += Math.PI;
            res1 = Math.Atan2(-m[2, 0], -c2);
        }
        else
        {
            res1 = Math.Atan2(-m[2, 0], c2);
        }

        double s1 = Math.Sin(res0);
        double c1 = Math.Cos(res0);
        double res2 = Math.Atan2((s1 * m[0, 2]) - (c1 * m[1, 2]), (c1 * m[1, 1]) - (s1 * m[0, 1]));

        return (res2, res1, res0);
    }

    /// <summary>Column-vector rotation about a single world axis (0=X, 1=Y, 2=Z).</summary>
    private static double[,] AxisRotation(int axis, double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return axis switch
        {
            0 => new[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } },
            1 => new[,] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } },
            _ => new[,] { { c, -s, 0 }, { s, c, 0 }, { 0, 0, 1 } },
        };
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                for (int k = 0; k < 3; k++)
                {
                    r[i, j] += a[i, k] * b[k, j];
                }
            }
        }

        return r;
    }

    /// <summary>
    /// <c>Geometry::rotation_transform</c>: column-vector <c>Rz·Ry·Rx</c> from an Euler triple.
    /// </summary>
    private static double[,] RotationTransform(double x, double y, double z) =>
        Multiply(Multiply(AxisRotation(2, z), AxisRotation(1, y)), AxisRotation(0, x));

    /// <summary>
    /// The viewer's orientation: three.js Euler order 'XYZ', column-vector <c>Rx·Ry·Rz</c>.
    /// </summary>
    private static double[,] ViewerRotation(double rx, double ry, double rz) =>
        Multiply(Multiply(AxisRotation(0, rx), AxisRotation(1, ry)), AxisRotation(2, rz));

    /// <summary>
    /// Simulate what OrcaSlicer actually does with the emitted flags, end to end: each
    /// <c>--rotate*</c> becomes a pure axis rotation, <c>ModelVolume::rotate</c> extracts an
    /// Euler triple from it and ADDS it to the accumulator, and <c>rotation_transform</c> finally
    /// composes the sum as <c>Rz·Ry·Rx</c>.
    /// <para>
    /// This is deliberately NOT "apply Rz·Ry·Rx to the emitted triple" — that models OrcaSlicer
    /// as consuming the triple verbatim and skips the extract-and-accumulate step, which is
    /// exactly where the negative-Z defect lives.
    /// </para>
    /// </summary>
    private static double[,] SimulateOrcaCli(string flags)
    {
        (double xDeg, double yDeg, double zDeg) = ParseRotationDegrees(flags);
        double sumX = 0, sumY = 0, sumZ = 0;

        foreach ((int axis, double deg) in new[] { (0, xDeg), (1, yDeg), (2, zDeg) })
        {
            if (Math.Abs(deg) < 1e-9)
            {
                continue;
            }

            (double ex, double ey, double ez) =
                ExtractEulerAnglesLikeOrca(AxisRotation(axis, deg * Math.PI / 180.0));
            sumX += ex;
            sumY += ey;
            sumZ += ez;
        }

        return RotationTransform(sumX, sumY, sumZ);
    }

    private static double MaxAbsDifference(double[,] a, double[,] b)
    {
        double worst = 0;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                worst = Math.Max(worst, Math.Abs(a[i, j] - b[i, j]));
            }
        }

        return worst;
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
