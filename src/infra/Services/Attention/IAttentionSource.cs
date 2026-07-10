using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos.Attention;

namespace Farm.Infrastructure.Services.Attention;

/// <summary>
/// Pluggable source of attention items. The composition service iterates every registered
/// <see cref="IAttentionSource"/> and merges the results into the unified feed.
/// </summary>
/// <remarks>
/// <para>
/// This is the explicit seam described in issue #707: F4 runout, F9 harvest extensions,
/// and any future sources land as new <see cref="IAttentionSource"/> implementations
/// without touching the composition service or controller.
/// </para>
/// <para>
/// Implementations MUST be robust to partial-source failure: if a source throws, the
/// composition service logs and returns the remaining sources' items. Implementations
/// SHOULD apply their own bounded time window and/or paging so a runaway source does
/// not flood the feed.
/// </para>
/// </remarks>
public interface IAttentionSource
{
    /// <summary>Deterministic name for logs and diagnostics.</summary>
    string SourceName { get; }

    /// <summary>
    /// Returns the current attention items produced by this source. Items are unsorted;
    /// the composition service applies the global ordering rules.
    /// </summary>
    Task<IReadOnlyList<AttentionItemDto>> GetItemsAsync(CancellationToken cancellationToken);
}
