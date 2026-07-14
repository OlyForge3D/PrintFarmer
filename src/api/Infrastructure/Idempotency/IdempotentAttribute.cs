using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Infrastructure.Idempotency;

/// <summary>
/// Marks a controller action as participating in the persistent
/// <c>Idempotency-Key</c> replay contract (issue #715).
///
/// <para>
/// Implements <see cref="IFilterFactory"/> so the attribute both declares the
/// canonical route key (<see cref="RouteKey"/>) and produces a
/// per-request <see cref="IdempotencyFilter"/> resolved from DI. This keeps the
/// filter pay-per-use — only decorated endpoints pay the cost of body buffering
/// and store lookup.
/// </para>
///
/// <para>
/// The <see cref="RouteKey"/> must be a stable string constant so a request
/// against a parameterized path (e.g. <c>/api/parts-inventory/RD-500/adjust</c>)
/// canonicalizes to the same identity regardless of the SKU value. Use the
/// constants from
/// <see cref="Farm.Infrastructure.Services.Idempotency.IdempotencyRouteKeys"/>.
/// </para>
///
/// <para>
/// The endpoint must sit behind <c>[Authorize]</c> — the filter needs an
/// authenticated user to key the store; anonymous requests bypass persistence
/// (executed normally, no replay support) rather than pretending to protect
/// an unauthenticated caller.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class IdempotentAttribute : Attribute, IFilterFactory, IOrderedFilter
{
    /// <summary>
    /// Canonical route key. Use one of the constants from
    /// <see cref="Farm.Infrastructure.Services.Idempotency.IdempotencyRouteKeys"/>.
    /// </summary>
    public string RouteKey { get; }

    /// <inheritdoc />
    public bool IsReusable => false;

    /// <inheritdoc />
    /// <remarks>
    /// Runs after authentication/authorization filters (which are ordered near
    /// <c>int.MinValue</c>) but before the model binder — the negative value
    /// ensures the filter fires early enough to buffer the request body for
    /// hashing before binding rewinds/consumes it.
    /// </remarks>
    public int Order { get; set; } = -500;

    /// <summary>Constructs the attribute with a canonical route key.</summary>
    public IdempotentAttribute(string routeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        RouteKey = routeKey;
    }

    /// <inheritdoc />
    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return serviceProvider.GetRequiredService<IdempotencyFilter>();
    }
}
