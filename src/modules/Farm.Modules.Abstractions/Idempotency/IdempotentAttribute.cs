using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Abstractions.Idempotency;

/// <summary>
/// Filter contract resolved by <see cref="IdempotentAttribute"/>. A module registers its own
/// implementation (buffering the request body, hashing it, and consulting its idempotency
/// store) via DI; the attribute never depends on a concrete filter type.
/// </summary>
[SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "DI marker interface used to resolve a module-owned IAsyncActionFilter without a compile-time reference to it.")]
public interface IIdempotencyFilter : IFilterMetadata
{
}

/// <summary>
/// Marks a controller action as participating in a persistent <c>Idempotency-Key</c> replay
/// contract.
/// </summary>
/// <remarks>
/// <para>
/// Self-contained sibling of
/// <c>Farm.Web.Api.Infrastructure.Idempotency.IdempotentAttribute</c>: implements
/// <see cref="IFilterFactory"/> so the attribute both declares the canonical route key
/// (<see cref="RouteKey"/>) and produces a per-request filter resolved from DI, without any
/// compile-time reference to the monolith's concrete <c>IdempotencyFilter</c> type -- a module
/// registers its own <see cref="IIdempotencyFilter"/> implementation instead. The existing
/// monolith attribute is untouched by this phase.
/// </para>
/// <para>
/// The <see cref="RouteKey"/> must be a stable string constant so a request against a
/// parameterized path (e.g. <c>/api/parts-inventory/RD-500/adjust</c>) canonicalizes to the same
/// identity regardless of the SKU value.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class IdempotentAttribute : Attribute, IFilterFactory, IOrderedFilter
{
    /// <summary>Canonical route key identifying this endpoint's idempotency scope.</summary>
    public string RouteKey { get; }

    /// <inheritdoc />
    public bool IsReusable => false;

    /// <inheritdoc />
    /// <remarks>
    /// Runs after authentication/authorization filters (which are ordered near
    /// <c>int.MinValue</c>) but before the model binder, matching the monolith's
    /// <c>IdempotentAttribute</c> default.
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
        return serviceProvider.GetRequiredService<IIdempotencyFilter>();
    }
}
