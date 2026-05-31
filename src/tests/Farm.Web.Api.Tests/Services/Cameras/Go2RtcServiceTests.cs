using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Settings;
using Farm.Settings;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Cameras;

/// <summary>
/// Unit tests for <see cref="Go2RtcService"/>.
/// </summary>
public class Go2RtcServiceTests
{
    private static Go2RtcService CreateService(
        Go2RtcSettings? settings,
        HttpMessageHandler? httpHandler = null)
    {
        var settingsService = new Mock<ISettingsService>(MockBehavior.Strict);
        settingsService.Setup(s => s.Get<Go2RtcSettings>()).Returns(settings);

        var factory = new Mock<IHttpClientFactory>(MockBehavior.Loose);
        if (httpHandler is not null)
        {
            factory.Setup(f => f.CreateClient(It.IsAny<string>()))
                   .Returns(new HttpClient(httpHandler));
        }

        var logger = new Mock<ILogger<Go2RtcService>>(MockBehavior.Loose);

        return new Go2RtcService(settingsService.Object, factory.Object, logger.Object);
    }

    #region IsEnabled

    [Fact]
    public void IsEnabled_WhenEnabledAndBaseUrlSet_ReturnsTrue()
    {
        var service = CreateService(new Go2RtcSettings { Enabled = true, BaseUrl = "http://go2rtc:1984" });

        service.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_WhenDisabled_ReturnsFalse()
    {
        var service = CreateService(new Go2RtcSettings { Enabled = false, BaseUrl = "http://go2rtc:1984" });

        service.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_WhenBaseUrlEmpty_ReturnsFalse()
    {
        var service = CreateService(new Go2RtcSettings { Enabled = true, BaseUrl = string.Empty });

        service.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_WhenBaseUrlWhitespace_ReturnsFalse()
    {
        var service = CreateService(new Go2RtcSettings { Enabled = true, BaseUrl = "   " });

        service.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_WhenSettingsNull_ReturnsFalse()
    {
        var service = CreateService(settings: null);

        service.IsEnabled.Should().BeFalse();
    }

    #endregion

    #region GetSnapshotUrl

    [Fact]
    public void GetSnapshotUrl_WhenEnabled_ReturnsExpectedUrl()
    {
        var cameraId = Guid.NewGuid();
        var service = CreateService(new Go2RtcSettings { Enabled = true, BaseUrl = "http://go2rtc:1984" });

        string? url = service.GetSnapshotUrl(cameraId);

        url.Should().Be($"http://go2rtc:1984/api/frame.jpeg?src={cameraId}");
    }

    [Fact]
    public void GetSnapshotUrl_WhenDisabled_ReturnsNull()
    {
        var service = CreateService(new Go2RtcSettings { Enabled = false, BaseUrl = "http://go2rtc:1984" });

        service.GetSnapshotUrl(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void GetSnapshotUrl_TrimsTrailingSlashFromBaseUrl()
    {
        var cameraId = Guid.NewGuid();
        var service = CreateService(new Go2RtcSettings { Enabled = true, BaseUrl = "http://go2rtc:1984/" });

        string? url = service.GetSnapshotUrl(cameraId);

        url.Should().NotContain("//api/");
        url.Should().Be($"http://go2rtc:1984/api/frame.jpeg?src={cameraId}");
    }

    #endregion

    #region AddStreamAsync

    [Fact]
    public async Task AddStreamAsync_WhenEnabled_ReturnsSnapshotUrl()
    {
        var cameraId = Guid.NewGuid();
        var handler = CreateSuccessHandler();
        var service = CreateService(
            new Go2RtcSettings { Enabled = true, BaseUrl = "http://go2rtc:1984" },
            handler);

        string? result = await service.AddStreamAsync(cameraId, "rtsp://cam:554/stream", CancellationToken.None);

        result.Should().Be($"http://go2rtc:1984/api/frame.jpeg?src={cameraId}");
    }

    [Fact]
    public async Task AddStreamAsync_WhenDisabled_ReturnsNull()
    {
        var service = CreateService(new Go2RtcSettings { Enabled = false, BaseUrl = "http://go2rtc:1984" });

        string? result = await service.AddStreamAsync(Guid.NewGuid(), "rtsp://cam:554/stream", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task AddStreamAsync_WhenHttpFails_ReturnsNull()
    {
        var handler = CreateFailureHandler(HttpStatusCode.InternalServerError);
        var service = CreateService(
            new Go2RtcSettings { Enabled = true, BaseUrl = "http://go2rtc:1984" },
            handler);

        string? result = await service.AddStreamAsync(Guid.NewGuid(), "rtsp://cam:554/stream", CancellationToken.None);

        result.Should().BeNull();
    }

    #endregion

    #region RemoveStreamAsync

    [Fact]
    public async Task RemoveStreamAsync_WhenEnabled_SendsDeleteRequest()
    {
        var cameraId = Guid.NewGuid();
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var service = CreateService(
            new Go2RtcSettings { Enabled = true, BaseUrl = "http://go2rtc:1984" },
            handlerMock.Object);

        await service.RemoveStreamAsync(cameraId, CancellationToken.None);

        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.Method == HttpMethod.Delete &&
                r.RequestUri!.ToString().Contains(cameraId.ToString())),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task RemoveStreamAsync_WhenDisabled_DoesNotSendRequest()
    {
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var service = CreateService(
            new Go2RtcSettings { Enabled = false, BaseUrl = "http://go2rtc:1984" },
            handlerMock.Object);

        await service.RemoveStreamAsync(Guid.NewGuid(), CancellationToken.None);

        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task RemoveStreamAsync_WhenHttpFails_DoesNotThrow()
    {
        var handler = CreateFailureHandler(HttpStatusCode.NotFound);
        var service = CreateService(
            new Go2RtcSettings { Enabled = true, BaseUrl = "http://go2rtc:1984" },
            handler);

        Func<Task> act = () => service.RemoveStreamAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    #endregion

    private static HttpMessageHandler CreateSuccessHandler()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
        return handlerMock.Object;
    }

    private static HttpMessageHandler CreateFailureHandler(HttpStatusCode statusCode)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode));
        return handlerMock.Object;
    }
}
