using Farm.Infrastructure.Domain.Sync;

namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Describes a single operation that could not be applied because it conflicts with the
/// current server state (#845). It carries both the safe server version and the submitted
/// version so the client can present or resolve the divergence. Only entities the caller is
/// authorized to write produce a conflict body — unauthorized writes are rejected outright —
/// so echoing the server version here never leaks another user's data.
/// </summary>
public class SyncConflictDto
{
    /// <summary>The kind of entity that conflicted.</summary>
    public SyncEntityType EntityType { get; set; }

    /// <summary>The conflicting entity id.</summary>
    public Guid EntityId { get; set; }

    /// <summary>Human-readable reason for the conflict.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The current server-side version, or an <c>Exists=false</c> marker when removed.</summary>
    public SyncConflictVersionDto? Server { get; set; }

    /// <summary>The client-submitted version that was rejected.</summary>
    public SyncConflictVersionDto? Submitted { get; set; }
}

/// <summary>
/// A lightweight snapshot of an entity version used in conflict reporting. Fields are
/// nullable so both collection and membership conflicts can reuse the shape without leaking
/// irrelevant defaults.
/// </summary>
public class SyncConflictVersionDto
{
    /// <summary>The entity revision, when known.</summary>
    public long? Revision { get; set; }

    /// <summary>The concurrency token (ETag), when known.</summary>
    public Guid? ConcurrencyToken { get; set; }

    /// <summary>The collection name, for collection conflicts.</summary>
    public string? Name { get; set; }

    /// <summary>The collection description, for collection conflicts.</summary>
    public string? Description { get; set; }

    /// <summary>The collection shared flag, for collection conflicts.</summary>
    public bool? IsShared { get; set; }

    /// <summary>Whether the entity currently exists on the server.</summary>
    public bool? Exists { get; set; }
}

/// <summary>
/// The HTTP 409 response body returned when a sync apply batch conflicts. The whole batch is
/// rolled back; <see cref="Conflicts"/> lists every conflicting operation and
/// <see cref="ServerRevision"/> is the current global head the client should re-pull from.
/// </summary>
public class SyncConflictResponseDto
{
    /// <summary>Short error summary.</summary>
    public string Error { get; set; } = "One or more operations conflict with the current server state";

    /// <summary>The full set of conflicts detected in the batch.</summary>
    public IReadOnlyList<SyncConflictDto> Conflicts { get; set; } = [];

    /// <summary>The current server revision (global head) to re-pull from.</summary>
    public long ServerRevision { get; set; }
}
