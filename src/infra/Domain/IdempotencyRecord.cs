using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Status of a persisted idempotency record.
/// <see cref="Processing"/> is stamped when a request first arrives with a given
/// (user, route, key) triple; <see cref="Completed"/> is stamped after the wrapped
/// mutation has finished and its response has been captured for exact replay.
/// </summary>
/// <remarks>
/// Persisted as a string via <c>HasConversion&lt;string&gt;()</c> so the enum can
/// evolve without a numeric-migration risk. Values are compared case-sensitively
/// against the canonical strings <c>"processing"</c> and <c>"completed"</c>.
/// </remarks>
public enum IdempotencyRecordStatus
{
    /// <summary>The mutation is in-flight; no replay body has been captured yet.</summary>
    Processing = 0,

    /// <summary>The mutation has finished; the stored response body is authoritative.</summary>
    Completed = 1,
}

/// <summary>
/// Persistent record of a single client-supplied <c>Idempotency-Key</c> against a
/// mutation route. Keyed by (<see cref="UserId"/>, <see cref="RouteKey"/>,
/// <see cref="IdempotencyKey"/>) — that triple must be unique. See issue #715 for
/// contract details.
///
/// Retention is 7 days measured from <see cref="CreatedAt"/> (UTC). Records older
/// than the window are ignored on read and pruned by
/// <c>IdempotencyRecordCleanupService</c>; they must never be mistaken for a
/// successful replay.
/// </summary>
public class IdempotencyRecord
{
    /// <summary>Surrogate primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Authenticated user identifier owning this record. Keys are strictly
    /// per-user: two different users may reuse the same client-supplied
    /// idempotency key without colliding.
    /// </summary>
    [MaxLength(256)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Canonical route key, e.g. <c>POST /api/parts-inventory/{sku}/adjust</c>.
    /// Different routes never share replay state even when a client reuses the
    /// same idempotency key across them.
    /// </summary>
    [MaxLength(200)]
    public string RouteKey { get; set; } = string.Empty;

    /// <summary>Client-supplied idempotency key.</summary>
    [MaxLength(200)]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 hex of the canonical request payload used to detect a conflicting
    /// retry of the same key with different content. 64 characters of hex.
    /// </summary>
    [MaxLength(64)]
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>Current lifecycle status. See <see cref="IdempotencyRecordStatus"/>.</summary>
    public IdempotencyRecordStatus Status { get; set; } = IdempotencyRecordStatus.Processing;

    /// <summary>HTTP status code captured from the completed response.</summary>
    public int? ResponseStatusCode { get; set; }

    /// <summary>Response Content-Type header captured from the completed response.</summary>
    [MaxLength(200)]
    public string? ResponseContentType { get; set; }

    /// <summary>Raw response body bytes captured for exact replay.</summary>
    public byte[]? ResponseBody { get; set; }

    /// <summary>
    /// Immutable UTC creation timestamp. Retention window is measured from this
    /// value; never mutated after the initial insert.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last update UTC timestamp; refreshed when the record transitions to <see cref="IdempotencyRecordStatus.Completed"/>.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
