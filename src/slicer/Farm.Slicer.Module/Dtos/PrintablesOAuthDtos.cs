namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Response payload for starting the Printables OAuth2 authorization flow.
/// </summary>
public sealed record PrintablesOAuthConnectResponseDto(
    string AuthorizationUrl,
    DateTime ExpiresAtUtc);

/// <summary>
/// Current linkage status for the caller's Printables OAuth2 account.
/// </summary>
public sealed record PrintablesOAuthStatusDto(
    bool IsLinked,
    DateTime? AccessTokenExpiresAtUtc,
    DateTime? LinkedAtUtc,
    bool HasRefreshToken,
    string? Scope);

/// <summary>
/// Read-only page returned by guarded authenticated Printables endpoints.
/// </summary>
public sealed record PrintablesAuthenticatedCursorPageDto(
    IReadOnlyList<PrintablesModelSummaryDto> Items,
    string? NextCursor,
    bool HasMore);
