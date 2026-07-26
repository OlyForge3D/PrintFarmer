using System.Collections.Generic;

namespace Farm.Infrastructure.Network;

/// <summary>
/// Configuration for the ASP.NET Core forwarded-headers middleware.
///
/// Bound from the <c>ForwardedHeaders</c> configuration section. When
/// <see cref="Enabled"/> is <c>false</c> (the default) no forwarding headers
/// are honored and rate-limit / audit code sees the real
/// <c>HttpContext.Connection.RemoteIpAddress</c>. When enabled, the operator
/// must explicitly list every trusted proxy IP or CIDR — the middleware only
/// honors <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c> if the immediate
/// connection is from a configured proxy.
///
/// Related: security issue #862 (spoofable <c>X-Forwarded-For</c> bypassed
/// per-IP authentication rate limits when trust was not gated).
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// Master switch. When <c>false</c>, forwarding headers are ignored and
    /// <c>Connection.RemoteIpAddress</c> is the source of truth for the
    /// client IP. Must be turned on explicitly by operators deploying behind
    /// a trusted reverse proxy (nginx, Traefik, cloud load balancer, etc.).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Individual IP addresses of trusted immediate proxies (IPv4 or IPv6).
    /// Example: <c>["127.0.0.1", "::1"]</c>.
    /// Entries that fail to parse are logged as warnings and skipped.
    /// </summary>
    public List<string> KnownProxies { get; set; } = new();

    /// <summary>
    /// CIDR networks of trusted immediate proxies.
    /// Example: <c>["10.0.0.0/8", "172.16.0.0/12"]</c>.
    /// Entries that fail to parse are logged as warnings and skipped.
    /// </summary>
    public List<string> KnownNetworks { get; set; } = new();

    /// <summary>
    /// Optional cap on the forwarded-header hop count. Matches the framework's
    /// <c>ForwardedHeadersOptions.ForwardLimit</c>.
    /// Defaults to 1 (single trusted proxy in front of the app).
    /// </summary>
    public int ForwardLimit { get; set; } = 1;
}
