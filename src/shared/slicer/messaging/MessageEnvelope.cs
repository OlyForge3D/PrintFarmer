using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Web.Shared.Slicer.Messaging;

/// <summary>
/// Standard message envelope for slicer job processing with idempotency support
/// Version: 1.0
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public record MessageEnvelope
{
    /// <summary>
    /// Envelope version for compatibility tracking
    /// </summary>
    public const string CurrentVersion = "1.0";

    /// <summary>
    /// Unique job identifier
    /// </summary>
    public Guid JobId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Type of slicer engine requested
    /// </summary>
    public SlicerEngineType SlicerType { get; init; }

    /// <summary>
    /// Job processing priority
    /// </summary>
    public SlicingJobPriority Priority { get; init; } = SlicingJobPriority.Normal;

    /// <summary>
    /// Attempt number for retry tracking (starts at 1)
    /// </summary>
    public int Attempt { get; init; } = 1;

    /// <summary>
    /// Correlation identifier for idempotency and request tracking
    /// </summary>
    public Guid CorrelationId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// SHA-256 checksum of job content for duplicate detection
    /// </summary>
    public string Checksum { get; init; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the job was first submitted
    /// </summary>
    public DateTime SubmittedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Message envelope version
    /// </summary>
    public string Version { get; init; } = CurrentVersion;

    /// <summary>
    /// Generate SHA-256 checksum from job content for idempotency
    /// </summary>
    /// <param name="content">Serializable content to hash</param>
    /// <returns>Base64-encoded SHA-256 hash</returns>
    public static string GenerateChecksum(object content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var json = JsonSerializer.Serialize(content, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Create envelope with calculated checksum for the provided job content
    /// </summary>
    /// <param name="jobContent">Job content to include in checksum calculation</param>
    /// <param name="slicerType">Slicer engine type</param>
    /// <param name="priority">Job priority</param>
    /// <param name="correlationId">Optional correlation ID (generates new if not provided)</param>
    /// <returns>MessageEnvelope with calculated checksum</returns>
    public static MessageEnvelope Create(
        object jobContent,
        SlicerEngineType slicerType,
        SlicingJobPriority priority = SlicingJobPriority.Normal,
        Guid? correlationId = null)
    {
        return new MessageEnvelope
        {
            SlicerType = slicerType,
            Priority = priority,
            CorrelationId = correlationId ?? Guid.NewGuid(),
            Checksum = GenerateChecksum(jobContent)
        };
    }

    /// <summary>
    /// Create retry envelope with incremented attempt number
    /// </summary>
    /// <param name="originalEnvelope">Original envelope to retry</param>
    /// <returns>New envelope with incremented attempt</returns>
    public static MessageEnvelope CreateRetry(MessageEnvelope originalEnvelope)
    {
        ArgumentNullException.ThrowIfNull(originalEnvelope);

        return originalEnvelope with
        {
            JobId = Guid.NewGuid(), // New job ID for retry
            Attempt = originalEnvelope.Attempt + 1,
            SubmittedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Validate envelope integrity
    /// </summary>
    /// <param name="jobContent">Job content to validate against checksum</param>
    /// <returns>True if envelope checksum matches job content</returns>
    public bool ValidateChecksum(object jobContent)
    {
        var expectedChecksum = GenerateChecksum(jobContent);
        return string.Equals(Checksum, expectedChecksum, StringComparison.Ordinal);
    }

    /// <summary>
    /// Check if this envelope represents a duplicate based on correlation ID and checksum
    /// </summary>
    /// <param name="other">Other envelope to compare</param>
    /// <returns>True if envelopes represent the same logical job</returns>
    public bool IsDuplicateOf(MessageEnvelope other)
    {
        if (other is null)
        {
            return false;
        }

        return CorrelationId == other.CorrelationId &&
               string.Equals(Checksum, other.Checksum, StringComparison.Ordinal);
    }
}

/// <summary>
/// Job content for checksum calculation - includes all fields that affect job processing
/// </summary>
public record SlicingJobContent
{
    public Guid UserId { get; init; }
    public Guid PrinterId { get; init; }
    public string ModelFileUrl { get; init; } = string.Empty; // remains string for stable checksum across versions
    public string ModelFileName { get; init; } = string.Empty;
    public SlicerEngineType SlicerEngine { get; init; }
    public SlicerProfileDto SlicerProfile { get; init; } = new();
    public SlicingJobPriority Priority { get; init; } = SlicingJobPriority.Normal;
    public Dictionary<string, object> Metadata { get; init; } = [];

    /// <summary>
    /// Create job content from SlicingJobRequest for checksum calculation
    /// </summary>
    /// <param name="request">Slicing job request</param>
    /// <returns>Job content for checksum</returns>
    public static SlicingJobContent FromRequest(SlicingJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new SlicingJobContent
        {
            UserId = request.UserId,
            PrinterId = request.PrinterId,
            ModelFileUrl = request.ModelFileUrl.ToString(),
            ModelFileName = request.ModelFileName,
            SlicerEngine = request.SlicerEngine,
            SlicerProfile = request.SlicerProfile,
            Priority = request.Priority,
            Metadata = request.Metadata
        };
    }
}
