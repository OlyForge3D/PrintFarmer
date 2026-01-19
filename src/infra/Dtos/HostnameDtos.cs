using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// Resolve hostname/IP utility
/// <summary>
/// Request to normalize and optionally resolve a printer server hostname.
/// </summary>
public record ResolveHostnameRequest(string ServerUrl, PrinterBackend Backend);

/// <summary>
/// Response containing normalized URL and resolved IP (if available).
/// </summary>
public record ResolveHostnameResponse(string NormalizedInputUrl, string? ResolvedIp, string ResolvedBaseUrl);
