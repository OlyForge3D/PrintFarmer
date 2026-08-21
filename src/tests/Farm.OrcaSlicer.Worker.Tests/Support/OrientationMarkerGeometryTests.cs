using Farm.OrcaSlicer.Worker.Tests.Support;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Fast, docker-free tests for <see cref="OrientationMarkerGeometry"/> — the marker solid and
/// independent expected-size calculation used by the real-OrcaSlicer-CLI rotation test
/// (<c>PinnedOrcaCliRotationTests</c> in <c>Farm.Web.IntegrationTests</c>, issue #1802).
/// </summary>
public class OrientationMarkerGeometryTests
{
    [Fact]
    public void ComputeExpectedSize_Identity_ReturnsRawFootprint()
    {
        (double x, double y, double z) = OrientationMarkerGeometry.ComputeExpectedSize(0, 0, 0);

        x.Should().BeApproximately(OrientationMarkerGeometry.LengthX, 1e-9);
        y.Should().BeApproximately(OrientationMarkerGeometry.WidthY, 1e-9);
        z.Should().BeApproximately(OrientationMarkerGeometry.HeightZ, 1e-9);
    }

    [Fact]
    public void ComputeExpectedSize_90DegreeZRotation_SwapsXAndY()
    {
        (double x, double y, double z) =
            OrientationMarkerGeometry.ComputeExpectedSize(0, 0, Math.PI / 2);

        x.Should().BeApproximately(OrientationMarkerGeometry.WidthY, 1e-9);
        y.Should().BeApproximately(OrientationMarkerGeometry.LengthX, 1e-9);
        z.Should().BeApproximately(OrientationMarkerGeometry.HeightZ, 1e-9);
    }

    [Fact]
    public void ComputeExpectedSize_90DegreeXRotation_SwapsYAndZ()
    {
        (double x, double y, double z) =
            OrientationMarkerGeometry.ComputeExpectedSize(Math.PI / 2, 0, 0);

        x.Should().BeApproximately(OrientationMarkerGeometry.LengthX, 1e-9);
        y.Should().BeApproximately(OrientationMarkerGeometry.HeightZ, 1e-9);
        z.Should().BeApproximately(OrientationMarkerGeometry.WidthY, 1e-9);
    }

    /// <summary>
    /// Pins the specific multi-axis, negative-Z rotation named in issue #1802
    /// ([22.92°, 51.57°, -74.48°]) so a future change to the marker geometry or the expected-size
    /// formula cannot silently drift without a human noticing the numbers moved.
    /// </summary>
    [Fact]
    public void ComputeExpectedSize_MultiAxisNegativeZRotation_MatchesHandComputedSize()
    {
        double rx = 22.92 * Math.PI / 180.0;
        double ry = 51.57 * Math.PI / 180.0;
        double rz = -74.48 * Math.PI / 180.0;

        (double x, double y, double z) = OrientationMarkerGeometry.ComputeExpectedSize(rx, ry, rz);

        // Every dimension must actually change from the raw footprint: a passing result here that
        // happened to equal (LengthX, WidthY, HeightZ) would indicate the rotation silently had no
        // effect, which is exactly the "flags emitted as raw angles" failure mode this suite
        // exists to catch downstream against the real CLI.
        Math.Abs(x - OrientationMarkerGeometry.LengthX).Should().BeGreaterThan(0.5);
        Math.Abs(y - OrientationMarkerGeometry.WidthY).Should().BeGreaterThan(0.5);
        Math.Abs(z - OrientationMarkerGeometry.HeightZ).Should().BeGreaterThan(0.5);

        x.Should().BeGreaterThan(0);
        y.Should().BeGreaterThan(0);
        z.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ComputeExpectedSize_IsRotationDirectionSensitive()
    {
        double rx = 22.92 * Math.PI / 180.0;
        double ry = 51.57 * Math.PI / 180.0;
        double rz = -74.48 * Math.PI / 180.0;

        (double positiveX, double positiveY, double positiveZ) =
            OrientationMarkerGeometry.ComputeExpectedSize(rx, ry, rz);
        (double negatedX, double negatedY, double negatedZ) =
            OrientationMarkerGeometry.ComputeExpectedSize(rx, ry, -rz);

        // Flipping the sign of Z alone must change the resulting size for this asymmetric marker;
        // this is what makes the shape sensitive to a reverted negative-Z correction.
        bool anyDiffers =
            Math.Abs(positiveX - negatedX) > 0.5 ||
            Math.Abs(positiveY - negatedY) > 0.5 ||
            Math.Abs(positiveZ - negatedZ) > 0.5;
        anyDiffers.Should().BeTrue();
    }

    [Fact]
    public void WriteBinaryStl_ProducesWellFormedFileWithExpectedTriangleCount()
    {
        string path = Path.Combine(Path.GetTempPath(), $"marker-{Guid.NewGuid():N}.stl");
        try
        {
            OrientationMarkerGeometry.WriteBinaryStl(path);

            byte[] bytes = File.ReadAllBytes(path);
            bytes.Length.Should().Be(84 + (4 * 50)); // header+count, then 4 triangles * 50 bytes each

            uint triangleCount = BitConverter.ToUInt32(bytes, 80);
            triangleCount.Should().Be(4);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
