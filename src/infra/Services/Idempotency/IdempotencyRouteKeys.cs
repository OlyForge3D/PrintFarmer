using System.Globalization;
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
/// <c>{constant}|{canonical resource identity}</c> and folds that into both the stored
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
    /// The discriminator is built from the <b>typed</b> route values, not the raw request
    /// path (issue #715, Hicks r3 blocker 1). ASP.NET Core's route constraints validate but
    /// do <b>not</b> canonicalize the matched segment, so two requests that bind to the exact
    /// same action and the exact same parsed arguments can still carry different raw path text
    /// — e.g. <c>/api/tasks/ABCDEF01-.../complete</c> and its lowercase form both parse to one
    /// <see cref="System.Guid"/>, and <c>{id:guid}</c> also tolerates braced/hyphenless forms.
    /// Keying idempotency off the raw path would mint two records for one logical resource and
    /// let a same-key retry that differs only in casing/format double-apply the mutation. We
    /// therefore canonicalize each route's identifying values:
    /// <list type="bullet">
    /// <item><description><see cref="PartsInventoryAdjust"/>: the normalized SKU
    /// (<see cref="PartInventoryIdentity.NormalizeSku"/>) — the domain resolves the target
    /// <c>PartInventory</c> case-insensitively, so <c>/abc/adjust</c> and <c>/ABC/adjust</c>
    /// are the same entity and must share one record.</description></item>
    /// <item><description><see cref="JobQueueHarvest"/> and <see cref="TaskComplete"/>: the
    /// canonical GUID ("D" form — lowercase, hyphenated), collapsing upper/lower/braced/
    /// hyphenless variants.</description></item>
    /// <item><description><see cref="PrinterToolheadSpoolBind"/>: the canonical GUID printer id
    /// plus the invariant-parsed integer toolhead index (collapsing leading zeros).</description></item>
    /// </list>
    /// If a route value is missing or fails to parse (defensive; the route constraints should
    /// already have rejected such a request), we fall back to <paramref name="resolvedPath"/>
    /// so the identity is never silently dropped.
    /// </para>
    /// </summary>
    /// <param name="routeConstant">One of the route-key constants on this class.</param>
    /// <param name="resolvedPath">
    /// The resolved <c>HttpRequest.Path</c> value, used as the fallback discriminator when a
    /// route's typed values are absent or unparseable.
    /// </param>
    /// <param name="routeValues">
    /// The matched route values (case-insensitive keys) projected to their string form, from
    /// which the per-route canonical identity is built.
    /// </param>
    public static string BuildEffectiveIdentity(
        string routeConstant,
        string resolvedPath,
        IReadOnlyDictionary<string, string?> routeValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeConstant);
        ArgumentNullException.ThrowIfNull(routeValues);

        switch (routeConstant)
        {
            case PartsInventoryAdjust:
                // Match the domain's canonical entity key: normalized (case-insensitive,
                // trimmed) SKU. This is the ONLY route variable for parts-adjust, so the
                // normalized SKU fully identifies the resource — no other path segment matters.
                string? sku = GetRouteValue(routeValues, "sku");
                if (!string.IsNullOrWhiteSpace(sku))
                {
                    return $"{routeConstant}|{PartInventoryIdentity.NormalizeSku(sku)}";
                }

                break;

            case JobQueueHarvest:
            case TaskComplete:
                // Single {id:guid} routes: fold the canonical GUID so upper/lower/braced forms
                // that bind to the same Guid share one idempotency record.
                if (TryCanonicalizeGuid(GetRouteValue(routeValues, "id"), out string canonicalId))
                {
                    return $"{routeConstant}|{canonicalId}";
                }

                break;

            case PrinterToolheadSpoolBind:
                // {id:guid}/toolheads/{toolheadIndex:int}: canonicalize both the GUID (casing/
                // format) and the invariant integer (leading zeros) so equivalent requests
                // resolve to one record.
                if (TryCanonicalizeGuid(GetRouteValue(routeValues, "id"), out string canonicalPrinterId)
                    && TryCanonicalizeInt(GetRouteValue(routeValues, "toolheadIndex"), out string canonicalToolhead))
                {
                    return $"{routeConstant}|{canonicalPrinterId}|{canonicalToolhead}";
                }

                break;

            default:
                break;
        }

        return $"{routeConstant}|{resolvedPath}";
    }

    private static string? GetRouteValue(IReadOnlyDictionary<string, string?> routeValues, string key)
        => routeValues.TryGetValue(key, out string? value) ? value : null;

    /// <summary>
    /// Parses <paramref name="raw"/> as a <see cref="System.Guid"/> and, on success, emits its
    /// canonical "D" form (lowercase, hyphenated). Returns <c>false</c> for null/blank or
    /// unparseable input so the caller can fall back to the raw path.
    /// </summary>
    private static bool TryCanonicalizeGuid(string? raw, out string canonical)
    {
        if (!string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw, out Guid parsed))
        {
            canonical = parsed.ToString("D");
            return true;
        }

        canonical = string.Empty;
        return false;
    }

    /// <summary>
    /// Parses <paramref name="raw"/> as an invariant <see cref="int"/> and, on success, emits
    /// its canonical decimal form (collapsing leading zeros). Returns <c>false</c> for
    /// null/blank or unparseable input so the caller can fall back to the raw path.
    /// </summary>
    private static bool TryCanonicalizeInt(string? raw, out string canonical)
    {
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            canonical = parsed.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        canonical = string.Empty;
        return false;
    }
}
