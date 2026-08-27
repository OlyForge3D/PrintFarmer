using Farm.Modules.PrintQueue.Controllers.Responses;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Controllers.Responses;

public class CameraUrlResultTests
{
    private const string StreamPath =
        "/api/printers/00000000-0000-0000-0000-000000000001/camera/stream";
    private const string SnapshotPath =
        "/api/printers/00000000-0000-0000-0000-000000000001/camera/snapshot";

    [Fact]
    public void Constructor_WithProxyRoutes_Succeeds()
    {
        var result = new CameraUrlResult(StreamPath, SnapshotPath);

        _ = result.StreamUrl.Should().Be(StreamPath);
        _ = result.SnapshotUrl.Should().Be(SnapshotPath);
    }

    [Fact]
    public void Constructor_WithNullRoutes_Succeeds()
    {
        var result = new CameraUrlResult(null, null);

        _ = result.StreamUrl.Should().BeNull();
        _ = result.SnapshotUrl.Should().BeNull();
    }

    [Theory]
    [InlineData("http://camera.internal/stream")]
    [InlineData("https://camera.example/stream")]
    [InlineData("")]
    [InlineData("/camera/stream")]
    [InlineData("/api/printers/not-a-guid/camera/stream")]
    [InlineData("/api/printers/00000000-0000-0000-0000-000000000001/camera/snapshot")]
    [InlineData("/api/printers/00000000-0000-0000-0000-000000000001/camera/stream?target=private")]
    public void Constructor_WithNonProxyRoute_Throws(string route)
    {
        Action create = () => _ = new CameraUrlResult(route, null);

        _ = create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CameraUrlResult_IsValueComparableAndDeconstructable()
    {
        var first = new CameraUrlResult(StreamPath, SnapshotPath);
        var second = new CameraUrlResult(StreamPath, SnapshotPath);

        _ = first.Should().Be(second);
        (string? streamUrl, string? snapshotUrl) = first;
        _ = streamUrl.Should().Be(StreamPath);
        _ = snapshotUrl.Should().Be(SnapshotPath);
    }
}
