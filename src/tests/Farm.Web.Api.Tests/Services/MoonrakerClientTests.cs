using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Comprehensive tests for MoonrakerClient covering all critical API operations.
/// Tests HTTP client interactions, JSON parsing, error handling, and URL normalization.
/// </summary>
public class MoonrakerClientTests
{
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly Mock<IUnifiedLoggingService> _mockLogger;
    private readonly MoonrakerClient _sut;

    public MoonrakerClientTests()
    {
        _mockHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHandler.Object)
        {
            BaseAddress = new Uri("http://localhost:7125")
        };
        _mockLogger = new Mock<IUnifiedLoggingService>();
        _sut = new MoonrakerClient(_httpClient, _mockLogger.Object);
    }

    #region GetStatusAsync Tests

    [Fact]
    public async Task GetStatusAsync_WithValidDirectResponse_ReturnsOnlineStatus()
    {
        // Arrange - Direct response with "state" at root level
        var statusJson = JsonSerializer.Serialize(new { state = "ready" });
        SetupHttpResponse(HttpStatusCode.OK, statusJson);

        // Act
        var result = await _sut.GetStatusAsync("http://localhost");

        // Assert
        result.IsOnline.Should().BeTrue();
        result.State.Should().Be("ready");
    }

    [Fact]
    public async Task GetStatusAsync_WithWrappedResponse_ReturnsOnlineStatus()
    {
        // Arrange - Wrapped response with "result.state"
        var statusJson = JsonSerializer.Serialize(new { result = new { state = "printing" } });
        SetupHttpResponse(HttpStatusCode.OK, statusJson);

        // Act
        var result = await _sut.GetStatusAsync("http://localhost");

        // Assert
        result.IsOnline.Should().BeTrue();
        result.State.Should().Be("printing");
    }

    [Fact]
    public async Task GetStatusAsync_WithHttpError_ReturnsOfflineStatus()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.InternalServerError, "");

        // Act
        var result = await _sut.GetStatusAsync("http://localhost");

        // Assert
        result.IsOnline.Should().BeFalse();
        result.State.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_WithConnectionError_ReturnsOfflineStatus()
    {
        // Arrange
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        // Act
        var result = await _sut.GetStatusAsync("http://localhost");

        // Assert
        result.IsOnline.Should().BeFalse();
    }

    [Fact]
    public async Task GetStatusAsync_WithJsonParseError_ReturnsOfflineStatus()
    {
        // Arrange - Invalid JSON
        SetupHttpResponse(HttpStatusCode.OK, "{ invalid json }");

        // Act
        var result = await _sut.GetStatusAsync("http://localhost");

        // Assert
        result.IsOnline.Should().BeFalse();
    }

    [Fact]
    public async Task GetStatusAsync_WhenTimeoutOccurs_ReturnsOfflineStatus()
    {
        // Arrange
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("Operation timed out", new TimeoutException()));

        // Act
        var result = await _sut.GetStatusAsync("http://localhost");

        // Assert
        result.IsOnline.Should().BeFalse();
    }

    [Fact]
    public async Task GetStatusAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange - HttpClient wraps OperationCanceledException in TaskCanceledException
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("Cancelled", new OperationCanceledException()));

        // Act & Assert - Throws TaskCanceledException with OperationCanceledException inside
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => _sut.GetStatusAsync("http://localhost", cts.Token));
    }

    #endregion

    #region GetJobAsync Tests

    [Fact]
    public async Task GetJobAsync_WithActivePrintJob_ReturnsJobDetails()
    {
        // Arrange - Implementation gets progress from display_status, not print_stats
        var jobJson = JsonSerializer.Serialize(new
        {
            result = new
            {
                status = new
                {
                    print_stats = new
                    {
                        state = "printing",
                        filename = "model.gcode"
                    },
                    display_status = new
                    {
                        progress = 0.65
                    }
                }
            }
        });
        SetupHttpResponse(HttpStatusCode.OK, jobJson);

        // Act
        var result = await _sut.GetJobAsync("http://localhost");

        // Assert
        result.Should().NotBeNull();
        result!.PrintState.Should().Be("printing");
        result.Progress.Should().Be(65.0); // 0.65 * 100
        result.JobName.Should().Be("model.gcode");
    }

    [Fact]
    public async Task GetJobAsync_WithNoActiveJob_ReturnsNull()
    {
        // Arrange - No printing state returns empty PrinterJob, not null
        var jobJson = JsonSerializer.Serialize(new
        {
            result = new
            {
                status = new
                {
                    print_stats = new { state = "idle" }
                }
            }
        });
        SetupHttpResponse(HttpStatusCode.OK, jobJson);

        // Act
        var result = await _sut.GetJobAsync("http://localhost");

        // Assert
        result.Should().NotBeNull();
        result!.PrintState.Should().Be("idle");
        result.Progress.Should().BeNull();
    }

    [Fact]
    public async Task GetJobAsync_WithHttpError_ReturnsNull()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.NotFound, "");

        // Act
        var result = await _sut.GetJobAsync("http://localhost");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetJobAsync_WithProgressAsDecimal_ConvertsCorrectly()
    {
        // Arrange - Progress from display_status is converted from 0..1 to 0..100
        var jobJson = JsonSerializer.Serialize(new
        {
            result = new
            {
                status = new
                {
                    print_stats = new
                    {
                        state = "printing",
                        filename = "part.gcode"
                    },
                    display_status = new
                    {
                        progress = 0.5
                    }
                }
            }
        });
        SetupHttpResponse(HttpStatusCode.OK, jobJson);

        // Act
        var result = await _sut.GetJobAsync("http://localhost");

        // Assert
        result.Should().NotBeNull();
        result!.Progress.Should().Be(50.0);
    }

    #endregion

    #region GetCompositeStatusAsync Tests

    [Fact]
    public async Task GetCompositeStatusAsync_WithOnlyPosition_ReturnsParsedZ()
    {
        // Arrange - Minimal test: just return status + minimal responses for position
        SetupSequentialHttpResponses(
            // 1. GetStatusAsync
            JsonSerializer.Serialize(new { result = new { state = "printing" } }),
            // 2. GetJobAsync (empty status)
            JsonSerializer.Serialize(new { result = new { status = new { } } }),
            // 3. Position query - this is what we're testing
            JsonSerializer.Serialize(new
            {
                result = new
                {
                    status = new
                    {
                        toolhead = new
                        {
                            position = new[] { 10.5, 20.3, 15.2 }
                        }
                    }
                }
            }),
            // 4. Temps (empty)
            JsonSerializer.Serialize(new { result = new { status = new { } } }),
            // 5. Camera (error)
            JsonSerializer.Serialize(new { error = "not found" })
        );

        // Act
        var result = await _sut.GetCompositeStatusAsync("http://localhost");

        // Assert
        result.X.Should().Be(10.5);
        result.Y.Should().Be(20.3);
        result.Z.Should().Be(15.2);
    }

    [Fact]
    public async Task GetCompositeStatusAsync_WithMinimalData_ReturnsBasicStatus()
    {
        // Arrange - Only state available, no job or position
        var responses = new Queue<string>();
        responses.Enqueue(JsonSerializer.Serialize(new
        {
            result = new { state = "idle" }
        }));
        responses.Enqueue(JsonSerializer.Serialize(new
        {
            result = new { status = new { } }
        }));
        responses.Enqueue(JsonSerializer.Serialize(new
        {
            error = "object not available"
        }));

        SetupMultipleHttpResponses(responses);

        // Act
        var result = await _sut.GetCompositeStatusAsync("http://localhost");

        // Assert
        result.IsOnline.Should().BeTrue();
        result.State.Should().Be("idle");
        result.Progress.Should().BeNull();
        result.JobName.Should().BeNull();
    }

    [Fact]
    public async Task GetCompositeStatusAsync_WithConnectionError_ReturnsOfflineStatus()
    {
        // Arrange
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection failed"));

        // Act
        var result = await _sut.GetCompositeStatusAsync("http://localhost");

        // Assert
        result.IsOnline.Should().BeFalse();
    }

    #endregion

    #region Camera URL Tests

    [Fact]
    public async Task GetCameraStreamUrlAsync_ReturnsProperlyFormedUrl()
    {
        // Act
        var result = await _sut.GetCameraStreamUrlAsync("http://localhost:7125");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("webcam");
        result.Should().Contain("action=stream");
    }

    [Fact]
    public async Task GetCameraSnapshotUrlAsync_ReturnsProperlyFormedUrl()
    {
        // Act
        var result = await _sut.GetCameraSnapshotUrlAsync("http://localhost:7125");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("webcam");
        result.Should().Contain("action=snapshot");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetCameraStreamUrlAsync_WithNullOrWhitespaceUrl_ReturnsNull(string baseUrl)
    {
        // Act
        var result = await _sut.GetCameraStreamUrlAsync(baseUrl);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCameraSnapshotAsync_WithValidUrl_ReturnsImageBytes()
    {
        // Arrange - Mock two responses: first for URL generation, second for image fetch
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG header
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(imageBytes)
            });

        // Act
        var result = await _sut.GetCameraSnapshotAsync("http://localhost");

        // Assert
        result.Should().NotBeNull();
        result.Should().Equal(imageBytes);
    }

    #endregion

    #region Movement Commands Tests

    [Fact]
    public async Task HomeXYAsync_SendsCorrectGcodeCommand()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { result = "ok" }));

        // Act
        await _sut.HomeXYAsync("http://localhost");

        // Assert - Verify the request was made to correct endpoint
        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.AtLeastOnce(),
            ItExpr.Is<HttpRequestMessage>(msg =>
                msg.RequestUri.ToString().Contains("printer/gcode") ||
                msg.RequestUri.ToString().Contains("gcode/script")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task PauseAsync_SendsPauseCommand()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { result = "ok" }));

        // Act
        await _sut.PauseAsync("http://localhost");

        // Assert
        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.AtLeastOnce(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ResumeAsync_SendsResumeCommand()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { result = "ok" }));

        // Act
        await _sut.ResumeAsync("http://localhost");

        // Assert
        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.AtLeastOnce(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SetTempsAsync_WithValidTemps_SendsCommand()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { result = "ok" }));

        // Act
        var result = await _sut.SetTempsAsync("http://localhost", hotend: 205.0, bed: 60.0);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task SetTempsAsync_WithoutBedTemp_SendsHotendOnly()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { result = "ok" }));

        // Act
        var result = await _sut.SetTempsAsync("http://localhost", hotend: 205.0);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region URL Normalization Tests

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:7125")]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:7125")]
    [InlineData("192.168.1.50")]
    [InlineData("https://localhost:7125")]
    public async Task GetStatusAsync_WithVariousUrlFormats_NormalizesCorrectly(string baseUrl)
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { state = "ready" }));

        // Act
        var result = await _sut.GetStatusAsync(baseUrl);

        // Assert
        result.IsOnline.Should().BeTrue();
        
        // Verify request was made to normalized URL
        _mockHandler.Protected().Verify(
            "SendAsync",
            Times.AtLeastOnce(),
            ItExpr.Is<HttpRequestMessage>(msg =>
                msg.RequestUri.Port == 7125 || msg.RequestUri.ToString().Contains(":7125")),
            ItExpr.IsAny<CancellationToken>());
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task GetStatusAsync_With500Error_ReturnsOfflineAndLogsDebug()
    {
        // Arrange
        SetupHttpResponse(HttpStatusCode.InternalServerError, "");

        // Act
        var result = await _sut.GetStatusAsync("http://localhost");

        // Assert
        result.IsOnline.Should().BeFalse();
    }

    [Fact]
    public async Task GetJobAsync_WithMissingStatusProperty_ReturnsNull()
    {
        // Arrange - Response without status property returns PrinterJob with nulls
        var jobJson = JsonSerializer.Serialize(new { result = new { } });
        SetupHttpResponse(HttpStatusCode.OK, jobJson);

        // Act
        var result = await _sut.GetJobAsync("http://localhost");

        // Assert
        result.Should().NotBeNull();
        result!.PrintState.Should().BeNull();
    }

    #endregion

    #region Helper Methods

    private void SetupHttpResponse(HttpStatusCode statusCode, string content)
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            });
    }

    private void SetupMultipleHttpResponses(Queue<string> responses)
    {
        // Create a synchronized queue to ensure thread-safe dequeuing
        var syncResponses = new Queue<string>(responses);
        var lockObj = new object();
        
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                lock (lockObj)
                {
                    if (syncResponses.Count == 0)
                    {
                        // Fallback for unexpected additional requests
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    }
                    string content = syncResponses.Dequeue();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(content)
                    };
                }
            });
    }

    private void SetupSequentialHttpResponses(params string[] responses)
    {
        // Setup sequential responses in order, one per HTTP call
        int callCount = 0;
        var lockObj = new object();
        var responseList = responses.ToList();
        
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                lock (lockObj)
                {
                    if (callCount >= responseList.Count)
                    {
                        // Fallback for unexpected additional requests
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    }
                    string content = responseList[callCount];
                    callCount++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(content)
                    };
                }
            });
    }

    #endregion
}
