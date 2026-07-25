using Farm.Web.Api.Controllers.Responses;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Controllers.Responses;

public class CameraUrlResultTests
{
    [Fact]
    public void Constructor_WithBothUrls_Succeeds()
    {
        var result = new CameraUrlResult(
            StreamUrl: "http://example.com/stream",
            SnapshotUrl: "http://example.com/snapshot"
        );

        result.StreamUrl.Should().Be("http://example.com/stream");
        result.SnapshotUrl.Should().Be("http://example.com/snapshot");
    }

    [Fact]
    public void Constructor_WithNullUrls_Succeeds()
    {
        var result = new CameraUrlResult(
            StreamUrl: null,
            SnapshotUrl: null
        );

        result.StreamUrl.Should().BeNull();
        result.SnapshotUrl.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithStreamUrlOnly_Succeeds()
    {
        var result = new CameraUrlResult(
            StreamUrl: "http://example.com/stream",
            SnapshotUrl: null
        );

        result.StreamUrl.Should().Be("http://example.com/stream");
        result.SnapshotUrl.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithSnapshotUrlOnly_Succeeds()
    {
        var result = new CameraUrlResult(
            StreamUrl: null,
            SnapshotUrl: "http://example.com/snapshot"
        );

        result.StreamUrl.Should().BeNull();
        result.SnapshotUrl.Should().Be("http://example.com/snapshot");
    }

    [Fact]
    public void CameraUrlResult_IsRecord()
    {
        var result1 = new CameraUrlResult("http://stream", "http://snapshot");
        var result2 = new CameraUrlResult("http://stream", "http://snapshot");

        result1.Equals(result2).Should().BeTrue();
    }

    [Fact]
    public void CameraUrlResult_DifferentStreamUrls_AreNotEqual()
    {
        var result1 = new CameraUrlResult("http://stream1", "http://snapshot");
        var result2 = new CameraUrlResult("http://stream2", "http://snapshot");

        result1.Equals(result2).Should().BeFalse();
    }

    [Fact]
    public void CameraUrlResult_DifferentSnapshotUrls_AreNotEqual()
    {
        var result1 = new CameraUrlResult("http://stream", "http://snapshot1");
        var result2 = new CameraUrlResult("http://stream", "http://snapshot2");

        result1.Equals(result2).Should().BeFalse();
    }

    [Fact]
    public void CameraUrlResult_CanBeDeconstructed()
    {
        var result = new CameraUrlResult("http://stream", "http://snapshot");

        (string? streamUrl, string? snapshotUrl) = result;

        streamUrl.Should().Be("http://stream");
        snapshotUrl.Should().Be("http://snapshot");
    }

    [Fact]
    public void CameraUrlResult_WithHttpsUrls()
    {
        var result = new CameraUrlResult(
            StreamUrl: "https://secure.example.com/stream",
            SnapshotUrl: "https://secure.example.com/snapshot"
        );

        result.StreamUrl.Should().StartWith("https://");
        result.SnapshotUrl.Should().StartWith("https://");
    }

    [Fact]
    public void CameraUrlResult_WithLocalUrls()
    {
        var result = new CameraUrlResult(
            StreamUrl: "http://localhost:8080/stream",
            SnapshotUrl: "http://localhost:8080/snapshot"
        );

        result.StreamUrl.Should().Contain("localhost");
        result.SnapshotUrl.Should().Contain("localhost");
    }

    [Fact]
    public void CameraUrlResult_WithEmptyStrings()
    {
        var result = new CameraUrlResult(
            StreamUrl: "",
            SnapshotUrl: ""
        );

        result.StreamUrl.Should().Be("");
        result.SnapshotUrl.Should().Be("");
    }

    [Fact]
    public void CameraUrlResult_ToString_IncludesUrls()
    {
        var result = new CameraUrlResult("http://stream", "http://snapshot");

        result.ToString().Should().Contain("http://stream");
        result.ToString().Should().Contain("http://snapshot");
    }

    [Fact]
    public void CameraUrlResult_GetHashCode_ConsistentForSameValues()
    {
        var result1 = new CameraUrlResult("http://stream", "http://snapshot");
        var result2 = new CameraUrlResult("http://stream", "http://snapshot");

        result1.GetHashCode().Should().Be(result2.GetHashCode());
    }

    [Fact]
    public void CameraUrlResult_WithComplexUrls()
    {
        string streamUrl = "http://camera.local:8080/stream?quality=high&format=mjpeg";
        string snapshotUrl = "http://camera.local:8080/snapshot?quality=high";

        var result = new CameraUrlResult(streamUrl, snapshotUrl);

        result.StreamUrl.Should().Contain("quality=high");
        result.SnapshotUrl.Should().Contain("quality=high");
    }
}
