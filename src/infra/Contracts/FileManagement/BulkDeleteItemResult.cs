using System;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.FileManagement;

/// <summary>
/// Outcome of one distinct ID in a local-library bulk delete request.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BulkDeleteItemStatus>))]
public enum BulkDeleteItemStatus
{
    /// <summary>The database record and its stored files were removed.</summary>
    Deleted,

    /// <summary>The database record was removed but stored-file cleanup did not finish.</summary>
    DeletedCleanupIncomplete,

    /// <summary>No deletable record exists for the ID.</summary>
    NotFound,

    /// <summary>The item was intentionally not deleted, for example because active jobs use it.</summary>
    Blocked,

    /// <summary>The delete was attempted and did not take effect; the record is retained.</summary>
    Failed,

    /// <summary>The server cannot prove whether the delete took effect.</summary>
    Unknown,
}

/// <summary>
/// Stable machine-readable reason codes for <see cref="BulkDeleteItemResult.Reason"/>.
/// Values never contain storage paths or exception text.
/// </summary>
public static class BulkDeleteItemReasons
{
    /// <summary>Active queue jobs reference the G-code file.</summary>
    public const string ActiveJobs = "ActiveJobs";

    /// <summary>The stored path is unsafe or cannot be resolved inside the library root.</summary>
    public const string UnsafePath = "UnsafePath";

    /// <summary>Removing the stored files failed before the record was removed.</summary>
    public const string StorageFailed = "StorageFailed";

    /// <summary>A database read or write failed and was rolled back.</summary>
    public const string DatabaseFailed = "DatabaseFailed";

    /// <summary>The stored files were removed but the record could not be removed.</summary>
    public const string FilesRemovedRecordRetained = "FilesRemovedRecordRetained";

    /// <summary>The database commit failed in a way that may or may not have applied.</summary>
    public const string CommitOutcomeUnknown = "CommitOutcomeUnknown";

    /// <summary>The record was removed but the primary stored file could not be deleted.</summary>
    public const string FileCleanupFailed = "FileCleanupFailed";

    /// <summary>The record was removed but the thumbnail could not be deleted.</summary>
    public const string ThumbnailCleanupFailed = "ThumbnailCleanupFailed";
}

/// <summary>
/// Per-ID result returned by the local-library bulk delete routes.
/// </summary>
/// <param name="Id">The requested ID.</param>
/// <param name="Status">The outcome for this ID.</param>
/// <param name="Reason">Optional reason code from <see cref="BulkDeleteItemReasons"/>.</param>
public sealed record BulkDeleteItemResult(
    Guid Id,
    BulkDeleteItemStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null)
{
    /// <summary>Whether the database record no longer exists because of this request.</summary>
    [JsonIgnore]
    public bool RecordRemoved =>
        Status is BulkDeleteItemStatus.Deleted or BulkDeleteItemStatus.DeletedCleanupIncomplete;
}
