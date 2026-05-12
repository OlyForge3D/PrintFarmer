using System;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Manages go2rtc stream registration for RTSP cameras.
/// When go2rtc is enabled, syncs Camera entities with go2rtc streams
/// and derives snapshot URLs from the go2rtc API.
/// </summary>
public interface IGo2RtcService
{
    /// <summary>
    /// Registers or updates an RTSP stream in go2rtc for the given camera.
    /// Updates the camera's SnapshotUrl to point to go2rtc's frame endpoint.
    /// No-op when go2rtc is disabled.
    /// </summary>
    /// <param name="cameraId">The camera entity ID (used as stream name).</param>
    /// <param name="rtspUrl">The RTSP stream URL to register.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The go2rtc-derived snapshot URL, or null if go2rtc is disabled.</returns>
    Task<string?> AddStreamAsync(Guid cameraId, string rtspUrl, CancellationToken ct);

    /// <summary>
    /// Removes a stream from go2rtc for the given camera.
    /// No-op when go2rtc is disabled.
    /// </summary>
    /// <param name="cameraId">The camera entity ID (used as stream name).</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveStreamAsync(Guid cameraId, CancellationToken ct);

    /// <summary>
    /// Returns whether go2rtc integration is currently enabled and configured.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets the snapshot URL for a camera via go2rtc.
    /// </summary>
    /// <param name="cameraId">The camera entity ID.</param>
    /// <returns>The snapshot URL, or null if go2rtc is disabled.</returns>
    string? GetSnapshotUrl(Guid cameraId);
}
