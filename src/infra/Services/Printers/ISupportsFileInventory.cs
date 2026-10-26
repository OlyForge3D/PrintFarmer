using System.Collections.Immutable;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Metadata-only inventory authority, deliberately separate from the best-effort legacy file list.
/// Only explicitly complete coverage may be used to infer absence. No file bytes are requested.
/// </summary>
/// <remarks>
/// Moonraker covers native gcodes; OctoPrint covers recursively listed local machinecode;
/// PrusaLink covers PRINT_FILE entries in declared available storage, traversing every folder.
/// SDCP and FlashForge have no strict inventory capability: their legacy lists do not prove
/// recursive completion. Unsupported or incomplete coverage must never authorize absence.
/// </remarks>
public interface ISupportsFileInventory
{
    /// <summary>Enumerates provider-declared storage within bounded request, entry, depth and time budgets.</summary>
    Task<PrinterFileInventoryResult> GetFileInventoryAsync(
        string baseUrl,
        PrinterCredential? credential = null,
        PrinterFileInventoryOptions? options = null,
        CancellationToken ct = default);
}

/// <summary>Authority of a metadata inventory attempt, not the number of files returned.</summary>
public enum PrinterFileInventoryOutcome
{
    Complete,
    Partial,
    Unavailable,
    Unsupported,
    Cancelled
}

/// <summary>Bounded non-sensitive diagnostics; never includes URLs, credentials or response bodies.</summary>
public enum PrinterFileInventoryReason
{
    None,
    Unsupported,
    AuthenticationRequired,
    TransportFailure,
    Timeout,
    Cancelled,
    MalformedResponse,
    IncompleteResponse,
    InvalidPath,
    RequestLimit,
    EntryLimit,
    DepthLimit,
    ResponseLimit,
    StorageUnavailable
}

/// <summary>Listing evidence proving traversal completion for a storage root or relative subtree.</summary>
public enum PrinterFileInventoryEvidence
{
    None,
    NativeRecursiveListing,
    TraversedDirectories
}

/// <summary>One observed remote file. Null metadata means unknown, never zero or local ownership.</summary>
public sealed record PrinterFileInventoryEntry(
    string StorageRoot,
    string RelativePath,
    long? SizeBytes = null,
    DateTimeOffset? ModifiedAt = null,
    string? ProviderVersion = null,
    string? ProviderHash = null,
    string? ModifiedValue = null);

/// <summary>Completion applies only to this case-preserving root and relative subtree (empty means root).</summary>
public sealed record PrinterFileInventoryCoverage(
    string StorageRoot,
    string Subtree,
    bool IsComplete,
    PrinterFileInventoryEvidence Evidence);

/// <summary>Immutable observations and authority evidence from a single bounded scan.</summary>
public sealed record PrinterFileInventoryResult(
    PrinterFileInventoryOutcome Outcome,
    PrinterFileInventoryReason Reason,
    ImmutableArray<PrinterFileInventoryEntry> Entries,
    ImmutableArray<PrinterFileInventoryCoverage> Coverage)
{
    /// <summary>Explicitly fails closed for a backend without a native authoritative inventory.</summary>
    public static PrinterFileInventoryResult Unsupported() =>
        new(PrinterFileInventoryOutcome.Unsupported, PrinterFileInventoryReason.Unsupported, [], []);
}

/// <summary>Caller may reduce, but never exceed, the scan safety ceilings.</summary>
public sealed record PrinterFileInventoryOptions
{
    public int MaxRequests { get; init; } = 100;

    public int MaxEntries { get; init; } = 10_000;

    public int MaxDepth { get; init; } = 16;

    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Rejects invalid configuration instead of silently permitting an unbounded scan.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRequests, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRequests, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEntries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxEntries, 10_000);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxDepth, 16);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Deadline, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Deadline, TimeSpan.FromSeconds(120));
    }
}
