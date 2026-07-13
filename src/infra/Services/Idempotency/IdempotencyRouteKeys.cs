namespace Farm.Infrastructure.Services.Idempotency;

/// <summary>
/// Canonical route-key <b>prefixes</b> for endpoints that participate in the
/// <c>Idempotency-Key</c> header contract (issue #715).
///
/// <para>
/// Each constant is a stable method+template string identifying the endpoint
/// shape. It is <b>not</b> the full idempotency identity on its own: because the
/// template is shared by every <c>{id}</c>/<c>{sku}</c>/<c>{toolheadIndex}</c>,
/// using it alone would let one client key silently replay across different
/// resources (or, for empty-body actions like TaskComplete, silently drop the
/// second mutation). The filter therefore composes the effective route key as
/// <c>{constant}|{resolved request path}</c> and folds that into both the stored
/// <c>RouteKey</c> column and the request hash. The composite unique index also
/// includes <c>UserId</c>, so two users may reuse a key without colliding.
/// </para>
/// </summary>
public static class IdempotencyRouteKeys
{
    /// <summary>Prefix for <c>POST /api/parts-inventory/{sku}/adjust</c>.</summary>
    public const string PartsInventoryAdjust = "POST /api/parts-inventory/{sku}/adjust";

    /// <summary>Prefix for <c>POST /api/job-queue/{id}/harvest</c>.</summary>
    public const string JobQueueHarvest = "POST /api/job-queue/{id}/harvest";

    /// <summary>Prefix for <c>POST /api/tasks/{id}/complete</c>.</summary>
    public const string TaskComplete = "POST /api/tasks/{id}/complete";

    /// <summary>Prefix for <c>PUT /api/printers/{id}/toolheads/{toolheadIndex}/spool</c>.</summary>
    public const string PrinterToolheadSpoolBind = "PUT /api/printers/{id}/toolheads/{toolheadIndex}/spool";
}
