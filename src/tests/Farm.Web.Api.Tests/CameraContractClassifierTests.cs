using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cameras;

namespace Farm.Web.Api.Tests;

public class CameraContractClassifierTests
{
    [Fact]
    public void GetAccessMode_WhenStreamUnsupportedAndSnapshotPresent_ReturnsSnapshotOnly()
    {
        CameraAccessMode result = CameraContractClassifier.GetAccessMode(
            "ftp://camera.local/live",
            "http://camera.local/snapshot.jpg");

        result.Should().Be(CameraAccessMode.SnapshotOnly);
    }
}
