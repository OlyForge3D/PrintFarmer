using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain;
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

    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Serialize_WithContractHints_EmitsStringEnums()
    {
        var result = new CameraUrlResult(StreamPath, SnapshotPath)
        {
            AccessMode = CameraAccessMode.StreamAndSnapshot,
            StreamFormat = CameraStreamFormat.Mjpeg,
            SnapshotStrategy = CameraSnapshotStrategy.DirectUrl,
        };

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(result, ApiJson));

        _ = json.RootElement.GetProperty("streamUrl").GetString().Should().Be(StreamPath);
        _ = json.RootElement.GetProperty("accessMode").GetString().Should().Be("StreamAndSnapshot");
        _ = json.RootElement.GetProperty("streamFormat").GetString().Should().Be("Mjpeg");
        _ = json.RootElement.GetProperty("snapshotStrategy").GetString().Should().Be("DirectUrl");
    }

    [Fact]
    public void Serialize_WithoutContractHints_OmitsOptionalFields()
    {
        var result = new CameraUrlResult(StreamPath, null);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(result, ApiJson));

        _ = json.RootElement.TryGetProperty("accessMode", out _).Should().BeFalse();
        _ = json.RootElement.TryGetProperty("streamFormat", out _).Should().BeFalse();
        _ = json.RootElement.TryGetProperty("snapshotStrategy", out _).Should().BeFalse();
        _ = json.RootElement.GetProperty("streamUrl").GetString().Should().Be(StreamPath);
    }
}
