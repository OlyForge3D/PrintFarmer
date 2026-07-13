namespace Farm.Infrastructure.Services.Idempotency;

/// <summary>
/// Canonical route-key constants for endpoints that participate in the
/// <c>Idempotency-Key</c> header contract (issue #715).
///
/// Kept as string constants (rather than derived from
/// <see cref="Microsoft.AspNetCore.Http.HttpRequest.Path"/>) so that a
/// parameterized path like <c>/api/parts-inventory/RD-500-BLACK/adjust</c>
/// canonicalizes to the same key regardless of the SKU value. Two different
/// SKUs may share a client-supplied idempotency key without colliding because
/// the store composite index also includes <c>UserId</c> — but the route key is
/// what distinguishes the same key used against, say, harvest vs. task
/// complete.
/// </summary>
public static class IdempotencyRouteKeys
{
    /// <summary><c>POST /api/parts-inventory/{sku}/adjust</c>.</summary>
    public const string PartsInventoryAdjust = "POST /api/parts-inventory/{sku}/adjust";

    /// <summary><c>POST /api/job-queue/{id}/harvest</c>.</summary>
    public const string JobQueueHarvest = "POST /api/job-queue/{id}/harvest";

    /// <summary><c>POST /api/tasks/{id}/complete</c>.</summary>
    public const string TaskComplete = "POST /api/tasks/{id}/complete";

    /// <summary><c>PUT /api/printers/{id}/toolheads/{toolheadIndex}/spool</c>.</summary>
    public const string PrinterToolheadSpoolBind = "PUT /api/printers/{id}/toolheads/{toolheadIndex}/spool";
}
