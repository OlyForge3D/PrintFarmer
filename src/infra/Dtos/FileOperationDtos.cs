namespace Farm.Infrastructure;

// File operations results (upload/print)
/// <summary>
/// Result of uploading a G-code file directly to a printer backend.
/// </summary>
public record UploadGcodeResultDto(string Message, string Filename);

/// <summary>
/// Result of a start print command issued to a backend.
/// </summary>
public record StartPrintResultDto(string Message, string Filename);
