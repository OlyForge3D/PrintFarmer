using Farm.Infrastructure.Services.Printers;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class PrinterClientBaseTests
{
    private class TestablePrinterClient : PrinterClientBase
    {
        // Expose protected methods for testing
        public string PublicNormalizeBaseUrl(string url, int defaultPort) => NormalizeBaseUrl(url, defaultPort);
        public string PublicNormalizeBaseUrl(Uri url, int defaultPort) => NormalizeBaseUrl(url, defaultPort);
        public string PublicNormalizeCameraUrl(string? url, string baseNorm) => NormalizeCameraUrl(url, baseNorm);
        public bool PublicIsLoopbackHost(string host) => IsLoopbackHost(host);
    }

    private readonly TestablePrinterClient _client = new();

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("example.com", false)]
    [InlineData("10.0.0.1", false)]
    public void IsLoopbackHost_IdentifiesLoopbackAddresses(string host, bool expectedIsLoopback)
    {
        // Act
        bool result = _client.PublicIsLoopbackHost(host);

        // Assert
        Assert.Equal(expectedIsLoopback, result);
    }

    [Fact]
    public void IsLoopbackHost_WithNullHost_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _client.PublicIsLoopbackHost(null!));
    }

    [Theory]
    [InlineData("localhost", 5000, "http://localhost:5000")]
    [InlineData("http://localhost", 5000, "http://localhost:5000")]
    [InlineData("https://localhost", 5000, "https://localhost")]
    [InlineData("http://192.168.1.1", 5000, "http://192.168.1.1:5000")]
    [InlineData("192.168.1.1", 8080, "http://192.168.1.1:8080")]
    [InlineData("http://example.com:9000", 5000, "http://example.com:9000")]
    [InlineData("https://example.com:443", 5000, "https://example.com")]
    public void NormalizeBaseUrl_String_NormalizesUrlWithPort(string url, int defaultPort, string expected)
    {
        // Act
        string result = _client.PublicNormalizeBaseUrl(url, defaultPort);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeBaseUrl_String_WithTrailingSlash_RemovesIt()
    {
        // Act
        string result = _client.PublicNormalizeBaseUrl("http://localhost:5000/", 5000);

        // Assert
        Assert.Equal("http://localhost:5000", result);
    }

    [Fact]
    public void NormalizeBaseUrl_String_WithWhitespace_Trims()
    {
        // Act
        string result = _client.PublicNormalizeBaseUrl("  http://localhost:5000  ", 5000);

        // Assert
        Assert.Equal("http://localhost:5000", result);
    }

    [Fact]
    public void NormalizeBaseUrl_String_WithEmptyString_ReturnsEmpty()
    {
        // Act
        string result = _client.PublicNormalizeBaseUrl(string.Empty, 5000);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void NormalizeBaseUrl_Uri_AddsDefaultPort()
    {
        // Arrange
        var uri = new Uri("http://localhost");

        // Act
        string result = _client.PublicNormalizeBaseUrl(uri, 5000);

        // Assert
        Assert.Equal("http://localhost:5000", result);
    }

    [Fact]
    public void NormalizeBaseUrl_Uri_PreservesExplicitPort()
    {
        // Arrange
        var uri = new Uri("http://localhost:8080");

        // Act
        string result = _client.PublicNormalizeBaseUrl(uri, 5000);

        // Assert
        Assert.Equal("http://localhost:8080", result);
    }

    [Fact]
    public void NormalizeBaseUrl_Uri_WithNullUri_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _client.PublicNormalizeBaseUrl((Uri)null!, 5000));
    }

    [Fact]
    public void NormalizeCameraUrl_WithAbsoluteLoopbackUrl_ReplacesHostWithBaseHost()
    {
        // Act
        string result = _client.PublicNormalizeCameraUrl("http://127.0.0.1:5000/camera.jpg", "http://192.168.1.100:5000");

        // Assert
        Assert.Equal("http://192.168.1.100:5000/camera.jpg", result);
    }

    [Fact]
    public void NormalizeCameraUrl_WithAbsoluteExternalUrl_ReturnsAsIs()
    {
        // Act
        string result = _client.PublicNormalizeCameraUrl("https://external.com/stream", "http://localhost:5000");

        // Assert
        Assert.Equal("https://external.com/stream", result);
    }

    [Fact]
    public void NormalizeCameraUrl_WithRelativeUrl_ResolvesAgainstBase()
    {
        // Act
        string result = _client.PublicNormalizeCameraUrl("/camera/stream", "http://192.168.1.100:5000");

        // Assert
        Assert.Equal("http://192.168.1.100:5000/camera/stream", result);
    }

    [Fact]
    public void NormalizeCameraUrl_WithRelativeUrlNoLeadingSlash_AddsSlash()
    {
        // Act
        string result = _client.PublicNormalizeCameraUrl("camera.jpg", "http://localhost:8000");

        // Assert
        Assert.Equal("http://localhost:8000/camera.jpg", result);
    }

    [Fact]
    public void NormalizeCameraUrl_WithNullOrEmpty_ReturnsEmpty()
    {
        // Act
        string resultNull = _client.PublicNormalizeCameraUrl(null, "http://localhost:5000");
        string resultEmpty = _client.PublicNormalizeCameraUrl(string.Empty, "http://localhost:5000");
        string resultWhitespace = _client.PublicNormalizeCameraUrl("   ", "http://localhost:5000");

        // Assert
        Assert.Empty(resultNull);
        Assert.Empty(resultEmpty);
        Assert.Empty(resultWhitespace);
    }

    [Fact]
    public void NormalizeCameraUrl_WithNullBase_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _client.PublicNormalizeCameraUrl("http://localhost/camera", null!));
    }

    [Fact]
    public void NormalizeCameraUrl_WithLocalhostAbsoluteUrl_AlignsSchemeWithBase()
    {
        // Act
        string result = _client.PublicNormalizeCameraUrl("http://127.0.0.1:5000/stream", "https://192.168.1.100:5000");

        // Assert
        // Should replace loopback with base host AND align scheme
        Assert.StartsWith("https://192.168.1.100", result);
        Assert.Contains("/stream", result);
    }

    [Fact]
    public void NormalizeCameraUrl_WithInvalidUrl_FallsBackToConservativeJoin()
    {
        // Act - uses malformed URL that fails Uri parsing
        string result = _client.PublicNormalizeCameraUrl("ht!tp://invalid", "http://localhost:5000");

        // Assert
        // Should fallback to simple string join
        Assert.Contains("localhost:5000", result);
    }
}
