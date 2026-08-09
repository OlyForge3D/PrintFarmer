namespace Farm.Infrastructure.Services.Notifications;

/// <summary>
/// VAPID (Voluntary Application Server Identification) keys used to authenticate
/// outbound Web Push deliveries and to advertise the public key for browser
/// subscription enrollment.
///
/// Bound from the <c>WebPush</c> configuration section (e.g. <c>WebPush:VapidPublicKey</c>,
/// settable via the standard ASP.NET Core double-underscore environment variable
/// convention as <c>WebPush__VapidPublicKey</c>). Falls back to the legacy
/// <c>VAPID_PUBLIC_KEY</c> / <c>VAPID_PRIVATE_KEY</c> / <c>VAPID_SUBJECT</c> flat
/// environment variables for backward compatibility with existing deployments that
/// predate this configuration section.
/// </summary>
public class VapidOptions
{
    /// <summary>Public VAPID key advertised to browsers for push subscription enrollment.</summary>
    public string? VapidPublicKey { get; set; }

    /// <summary>Private VAPID key used to sign outbound web push deliveries. Never logged or exposed via API.</summary>
    public string? VapidPrivateKey { get; set; }

    /// <summary>Contact subject (mailto: or https: URL) included in the VAPID JWT per RFC 8292.</summary>
    public string? VapidSubject { get; set; }

    /// <summary>True when both the public and private key are present and web push delivery can proceed.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(VapidPublicKey) && !string.IsNullOrWhiteSpace(VapidPrivateKey);
}
