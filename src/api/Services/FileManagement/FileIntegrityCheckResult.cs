namespace Farm.Web.Api.Services.FileManagement;

/// <summary>
/// Represents the result of a file integrity check.
/// </summary>
public record FileIntegrityCheckResult(
    bool IsValid,
    string? ErrorMessage,
    string? FailureReason);  // "Missing", "HashMismatch", "SizeMismatch", "PermissionDenied", "Unknown"
