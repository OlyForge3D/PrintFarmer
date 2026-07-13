namespace Farm.Infrastructure.Domain.Notifications;

/// <summary>
/// Native push registration for a specific mobile installation. One row per
/// <c>(UserId, InstallationId)</c>. See <c>docs/OPERATOR_NATIVE_PUSH.md</c> and issue #708.
/// </summary>
/// <remarks>
/// Rows are only removed by:
/// <list type="bullet">
///   <item>Explicit unregister (<c>DELETE /api/notifications/device-tokens</c>),</item>
///   <item>Provider signaling permanent invalidation (<c>410 Gone</c> / <c>BadDeviceToken</c>),</item>
///   <item>User deletion (cascade FK).</item>
/// </list>
/// Toggling the <c>OperatorFeatures.NativePushEnabled</c> flag off never mutates this table
/// — tokens are retained so re-enable resumes delivery without re-registration.
/// </remarks>
public sealed class DeviceToken
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning user (cascade delete).</summary>
    public Guid UserId { get; set; }

    /// <summary>Navigation to owning user.</summary>
    public User? User { get; set; }

    /// <summary>
    /// Per-installation identifier supplied by the mobile app. Together with
    /// <see cref="UserId"/> forms the upsert key so re-registration replaces the token
    /// atomically without leaving orphaned rows.
    /// </summary>
    public string InstallationId { get; set; } = string.Empty;

    /// <summary>Provider-issued device token (APNs hex today).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Client platform: <c>ios</c> today; <c>android</c> reserved.</summary>
    public string Platform { get; set; } = "ios";

    /// <summary>APNs environment: <c>development</c> or <c>production</c>.</summary>
    public string Environment { get; set; } = "production";

    /// <summary>
    /// App bundle identifier captured at registration. Recorded for diagnostics; the
    /// server-side <c>apns-topic</c> header used on send comes from server configuration
    /// (never from the client) so a hostile client cannot direct a push at another app.
    /// </summary>
    public string? AppBundleId { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last successful send timestamp (also updated on registration upsert).</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Last transient send-failure timestamp.</summary>
    public DateTime? LastFailureAt { get; set; }

    /// <summary>
    /// Count of consecutive send failures since the last success. Reset to zero on any
    /// successful send. When it reaches the configured threshold the row is soft-deactivated;
    /// a subsequent successful registration upsert re-activates it.
    /// </summary>
    public int ConsecutiveFailureCount { get; set; }

    /// <summary>Whether this token participates in the delivery fan-out.</summary>
    public bool IsActive { get; set; } = true;
}
