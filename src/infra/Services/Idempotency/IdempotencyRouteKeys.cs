using Farm.Infrastructure.Services.PartsInventory;

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

    /// <summary>
    /// Composes the effective idempotency identity — <c>{routeConstant}|{discriminator}</c>
    /// — that the filter folds into both the stored <c>RouteKey</c> column and the request
    /// hash. The discriminator distinguishes one client key reused across different
    /// resources under the same route template.
    ///
    /// <para>
    /// For every route <b>except</b> parts-adjust the discriminator is the already-resolved
    /// request path, which is inherently canonical for GUID/int route values. Parts-adjust
    /// is special (issue #715, Hicks r2 blocker 1): the domain resolves the target
    /// <c>PartInventory</c> by the <b>normalized</b> SKU (<see cref="PartInventoryIdentity.NormalizeSku"/>,
    /// case-insensitive + trimmed), so <c>/api/parts-inventory/abc/adjust</c> and
    /// <c>/api/parts-inventory/ABC/adjust</c> hit the <b>same</b> entity. Keying idempotency
    /// off the raw path would create two separate records for one logical resource and let a
    /// same-key retry that differs only in SKU casing double-apply the stock delta. We
    /// therefore fold the normalized SKU (not the raw path) into the identity for that route,
    /// making the idempotency identity agree with the domain's entity resolution.
    /// </para>
    /// </summary>
    /// <param name="routeConstant">One of the route-key constants on this class.</param>
    /// <param name="resolvedPath">The resolved <c>HttpRequest.Path</c> value.</param>
    /// <param name="partsInventorySku">
    /// The <c>{sku}</c> route value for the parts-adjust route, or <c>null</c> for any other
    /// route. Ignored unless <paramref name="routeConstant"/> is <see cref="PartsInventoryAdjust"/>.
    /// </param>
    public static string BuildEffectiveIdentity(string routeConstant, string resolvedPath, string? partsInventorySku)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeConstant);

        if (string.Equals(routeConstant, PartsInventoryAdjust, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(partsInventorySku))
        {
            // Match the domain's canonical entity key: normalized (case-insensitive,
            // trimmed) SKU. This is the ONLY route variable for parts-adjust, so the
            // normalized SKU fully identifies the resource — no other path segment matters.
            return $"{routeConstant}|{PartInventoryIdentity.NormalizeSku(partsInventorySku)}";
        }

        return $"{routeConstant}|{resolvedPath}";
    }
}
