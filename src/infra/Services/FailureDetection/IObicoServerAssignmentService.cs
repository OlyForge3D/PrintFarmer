using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// Manages automatic assignment of printers to Obico ML servers.
/// When a printer opts in to Obico monitoring, this service picks the
/// best available server based on health, capacity, and load balancing.
/// </summary>
public interface IObicoServerAssignmentService
{
    /// <summary>
    /// Assigns the best available Obico server to a printer.
    /// Picks the enabled, healthy server with the most available capacity.
    /// </summary>
    /// <param name="printerId">Printer to assign a server to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assigned ObicoServer, or null if no server is available.</returns>
    Task<ObicoServer?> AssignServerAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Removes the Obico server assignment from a printer (when opting out).
    /// </summary>
    /// <param name="printerId">Printer to unassign.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UnassignServerAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Rebalances all Obico-enabled printers across available servers.
    /// Useful after adding/removing servers or when server health changes.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of printers reassigned.</returns>
    Task<int> RebalanceAsync(CancellationToken ct = default);
}
