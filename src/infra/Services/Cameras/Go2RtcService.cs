using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Settings;
using Farm.Settings;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Manages go2rtc stream registration via its REST API.
/// Streams are keyed by Camera.Id for stable identification.
/// </summary>
public class Go2RtcService : IGo2RtcService
{
    private readonly ISettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<Go2RtcService> _logger;

    public Go2RtcService(
        ISettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        ILogger<Go2RtcService> logger)
    {
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsEnabled
    {
        get
        {
            Go2RtcSettings? settings = _settingsService.Get<Go2RtcSettings>();
            return settings is { Enabled: true } && !string.IsNullOrWhiteSpace(settings.BaseUrl);
        }
    }

    public string? GetSnapshotUrl(Guid cameraId)
    {
        Go2RtcSettings? settings = _settingsService.Get<Go2RtcSettings>();
        if (settings is not { Enabled: true } || string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return null;
        }

        string baseUrl = settings.BaseUrl.TrimEnd('/');
        return $"{baseUrl}/api/frame.jpeg?src={cameraId}";
    }

    public async Task<string?> AddStreamAsync(Guid cameraId, string rtspUrl, CancellationToken ct)
    {
        Go2RtcSettings? settings = _settingsService.Get<Go2RtcSettings>();
        if (settings is not { Enabled: true } || string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            _logger.LogDebug("[go2rtc] Disabled — skipping stream add for camera {CameraId}", cameraId);
            return null;
        }

        if (!CameraUrlValidator.IsUrlSafeForProbing(rtspUrl))
        {
            _logger.LogWarning("[go2rtc] Blocked unsafe RTSP URL for camera {CameraId}", cameraId);
            return null;
        }

        string baseUrl = settings.BaseUrl.TrimEnd('/');
        string streamName = cameraId.ToString();

        try
        {
            using HttpClient client = _httpClientFactory.CreateClient("go2rtc");
            string url = $"{baseUrl}/api/streams?src={streamName}&name={streamName}";

            // go2rtc PUT /api/streams?src={name}&name={name} with body = source URL
            using var request = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new StringContent(rtspUrl),
            };

            using HttpResponseMessage response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            string snapshotUrl = $"{baseUrl}/api/frame.jpeg?src={streamName}";
            _logger.LogInformation(
                "[go2rtc] Registered stream {StreamName} → {RtspUrl}, snapshot: {SnapshotUrl}",
                streamName, rtspUrl, snapshotUrl);

            return snapshotUrl;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[go2rtc] Failed to add stream {StreamName} for {RtspUrl}", streamName, rtspUrl);
            return null;
        }
    }

    public async Task RemoveStreamAsync(Guid cameraId, CancellationToken ct)
    {
        Go2RtcSettings? settings = _settingsService.Get<Go2RtcSettings>();
        if (settings is not { Enabled: true } || string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return;
        }

        string baseUrl = settings.BaseUrl.TrimEnd('/');
        string streamName = cameraId.ToString();

        try
        {
            using HttpClient client = _httpClientFactory.CreateClient("go2rtc");
            string url = $"{baseUrl}/api/streams?src={streamName}";
            using HttpResponseMessage response = await client.DeleteAsync(url, ct);

            _logger.LogInformation("[go2rtc] Removed stream {StreamName}", streamName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[go2rtc] Failed to remove stream {StreamName}", streamName);
        }
    }
}
