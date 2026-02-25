using System.Text.Json.Serialization;

namespace Farm.Web.Api.DTOs.SignalR;

public sealed record DispatchUploadProgressDto
{
    public required string JobId { get; init; }

    public required string PrinterId { get; init; }

    public required string FileName { get; init; }

    public required long BytesSent { get; init; }

    public required long TotalBytes { get; init; }

    public required bool IsCompleted { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsFailed { get; init; }

    /// <summary>
    /// Current stage of the upload-and-print workflow.
    /// Values: "uploading", "processing", "startingPrint", "completed", "failed".
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stage { get; init; }

    /// <summary>
    /// Human-readable error message when <see cref="IsFailed"/> is true.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }

    public int Percentage => TotalBytes > 0
        ? (int)Math.Min(100, Math.Round((double)BytesSent / TotalBytes * 100))
        : 0;
}
