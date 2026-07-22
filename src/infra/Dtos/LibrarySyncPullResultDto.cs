using Farm.Infrastructure.Domain.Sync;

namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Result of a cursor-based sync pull (#845). Carries the ordered page of journal changes,
/// an opaque continuation cursor, a flag indicating whether more changes are available, and
/// the current server revision so the client knows how far the global stream extends.
/// Property names serialize to camelCase and enums as strings to match the client contract.
/// </summary>
public class LibrarySyncPullResultDto
{
    /// <summary>The ordered page of changes, ascending by <see cref="LibrarySyncChangeDto.Revision"/>.</summary>
    public IReadOnlyList<LibrarySyncChangeDto> Changes { get; set; } = [];

    /// <summary>
    /// Opaque, server-issued continuation cursor to resume paging from. Null only when the
    /// stream is empty and no prior position was supplied. Treat as an opaque token.
    /// </summary>
    public string? NextCursor { get; set; }

    /// <summary>True when more changes remain beyond this page.</summary>
    public bool HasMore { get; set; }

    /// <summary>The highest revision currently in the journal (global head), or 0 when empty.</summary>
    public long ServerRevision { get; set; }
}
