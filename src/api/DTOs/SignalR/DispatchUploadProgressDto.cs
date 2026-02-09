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

    public int Percentage => TotalBytes > 0
        ? (int)Math.Min(100, Math.Round((double)BytesSent / TotalBytes * 100))
        : 0;
}
