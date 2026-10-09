using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cameras;

namespace Farm.Web.Api.Tests;

public class CameraContractClassifierTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" http://camera.local/snap.jpg ", "http://camera.local/snap.jpg")]
    public void NormalizeTarget_TreatsBlankAsUnconfiguredAndTrimsOthers(string? input, string? expected)
    {
        CameraContractClassifier.NormalizeTarget(input).Should().Be(expected);
    }

    [Fact]
    public void GetAccessMode_WhenStreamUnsupportedAndSnapshotPresent_ReturnsSnapshotOnly()
    {
        CameraAccessMode result = CameraContractClassifier.GetAccessMode(
            "ftp://camera.local/live",
            "http://camera.local/snapshot.jpg");

        result.Should().Be(CameraAccessMode.SnapshotOnly);
    }
}
