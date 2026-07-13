namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Deployment mode for the native-push sender. See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public enum NativePushMode
{
    /// <summary>Sender is a no-op. Default when nothing is configured.</summary>
    Disabled = 0,

    /// <summary>Forward typed envelopes to an OlyForge3D-hosted relay over HTTPS.</summary>
    Relay = 1,

    /// <summary>Sign JWTs locally and post to <c>api.push.apple.com</c>.</summary>
    Direct = 2,
}

/// <summary>
/// Configuration bound from the <c>NativePush</c> section. Provider secrets are read from
/// standard ASP.NET configuration providers (env / user-secrets / mounted files) and never
/// committed to the repository. See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public sealed class NativePushSettings
{
    /// <summary>Configuration section key: <c>NativePush</c>.</summary>
    public const string SectionName = "NativePush";

    /// <summary>Deployment mode; defaults to <see cref="NativePushMode.Disabled"/>.</summary>
    public NativePushMode Mode { get; set; } = NativePushMode.Disabled;

    /// <summary>
    /// Sender attempts before a give-up (must be ≥ 1). Retries only apply to transient
    /// failures (5xx / network); 4xx responses are terminal.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Consecutive failures before a device token is soft-deactivated. A subsequent
    /// successful registration upsert re-activates the row.
    /// </summary>
    public int FailureDeactivationThreshold { get; set; } = 5;

    /// <summary>
    /// Sliding-window size for per-user rate limiting (default 30s). At most
    /// <see cref="RateLimitPerUser"/> envelopes are emitted per user per window.
    /// </summary>
    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Max envelopes per user per <see cref="RateLimitWindow"/>. Default 20.</summary>
    public int RateLimitPerUser { get; set; } = 20;

    /// <summary>Deduplication cache window (default 60s). Drops repeats of the same envelope key.</summary>
    public TimeSpan DedupeWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Relay-mode settings (used when <see cref="Mode"/> is <see cref="NativePushMode.Relay"/>).</summary>
    public NativePushRelaySettings Relay { get; set; } = new();

    /// <summary>Direct-mode settings (used when <see cref="Mode"/> is <see cref="NativePushMode.Direct"/>).</summary>
    public NativePushApnsSettings Apns { get; set; } = new();
}

/// <summary>
/// Relay-mode settings. The relay owns the APNs provider key; the local backend only
/// forwards typed envelopes with a per-install bearer token.
/// </summary>
public sealed class NativePushRelaySettings
{
    /// <summary>HTTPS endpoint of the OlyForge3D-hosted relay.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Bearer token issued per install by OlyForge3D.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional installation identifier the relay uses for per-tenant accounting.</summary>
    public string? InstallationId { get; set; }
}

/// <summary>
/// Direct-mode settings. Used only when the operator explicitly signs their own build with
/// a self-issued .p8 key; the value never comes from OlyForge3D.
/// </summary>
public sealed class NativePushApnsSettings
{
    /// <summary>Apple developer team id (10-character).</summary>
    public string? TeamId { get; set; }

    /// <summary>APNs auth key id (10-character).</summary>
    public string? KeyId { get; set; }

    /// <summary>App bundle identifier — used verbatim as the <c>apns-topic</c> header.</summary>
    public string? BundleId { get; set; }

    /// <summary>Absolute path to the .p8 key file. Prefer this over inline PEM.</summary>
    public string? P8KeyPath { get; set; }

    /// <summary>Inline PEM contents of the .p8. Prefer <see cref="P8KeyPath"/>.</summary>
    public string? P8KeyPem { get; set; }

    /// <summary>Default APNs environment for direct mode: <c>production</c> or <c>development</c>.</summary>
    public string Environment { get; set; } = "production";
}
