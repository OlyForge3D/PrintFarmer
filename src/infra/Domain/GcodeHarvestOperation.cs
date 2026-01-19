using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

// G-code Harvesting System
public class GcodeHarvestOperation
{
    public Guid Id { get; set; }

    public Guid PrinterId { get; set; }

    public Printer Printer { get; set; } = null!;

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public GcodeHarvestStatus Status { get; set; }

    // Enhanced error tracking
    public string? ErrorMessage { get; set; } // User-friendly error message

    public string? ErrorType { get; set; } // ConnectionError, AuthenticationError, FileSystemError, ValidationError, UnknownError

    public string? ErrorPhase { get; set; } // Discovery, Download, Processing, Completion

    public string? ErrorDetails { get; set; } // JSON: { exceptionType, stackTrace, additionalInfo }

    public string? FailedResource { get; set; } // File path or URL that caused the failure

    public bool IsRetryable { get; set; } = false; // Whether this error can be retried

    public DateTime? ErrorOccurredAt { get; set; } // Exact timestamp of error

    // File statistics
    public int FilesFound { get; set; }

    public int FilesAdded { get; set; }

    public int FilesSkipped { get; set; } // Already in library

    public int FilesErrored { get; set; }

    public long TotalBytesProcessed { get; set; }

    // Harvest options
    public bool IncludeSubdirectories { get; set; } = true;

    public long? MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB default

    public DateTime? ModifiedAfter { get; set; } // Only harvest files modified after this date

    public string[]? FileExtensions { get; set; } // JSON stored list of allowed extensions (without dot)

    public long? MinFileSizeBytes { get; set; }

    public string? DuplicateHandling { get; set; }

    // Navigation property: Collection of discovered files in this operation
    // Cascade delete: If operation is deleted, discovered files are deleted (but GcodeFiles are protected by Restrict behavior)
    public ICollection<HarvestDiscoveredFile> DiscoveredFiles { get; set; } = new List<HarvestDiscoveredFile>();
}
