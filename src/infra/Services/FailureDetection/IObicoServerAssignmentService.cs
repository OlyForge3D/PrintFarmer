using Farm.Infrastructure.Settings;

namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>Reconciles durable print-lifetime Obico routes with observed printer activity.</summary>
public interface IObicoServerAssignmentService
{
    /// <summary>
    /// Resolves an existing print route for explicit manual analysis without allocating capacity.
    /// Global-only installations retain their legacy manual-analysis destination.
    /// </summary>
    Task<ObicoPrintRoute> GetRouteAsync(Guid printerId, ObicoSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Releases ended prints before allocating healthy pool capacity to new prints.
    /// Unknown/offline observations retain existing reservations without creating new ones.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ObicoPrintRoute>> ReconcileAsync(
        IReadOnlyCollection<ObicoPrintActivity> activities,
        ObicoSettings settings,
        CancellationToken ct = default);
}

/// <summary>
/// A print identity, or a confirmed end when the identity is null.
/// RetainExisting represents missing/offline telemetry, not a print end.
/// AdoptExternalKey allows a synthetic external job to take over its initial live-only identity.
/// </summary>
public sealed record ObicoPrintActivity(
    Guid PrinterId,
    string? PrintKey,
    bool RetainExisting = false,
    string? AdoptExternalKey = null);

/// <summary>A selected monitoring destination, or an actionable reason why it is unavailable.</summary>
public sealed record ObicoPrintRoute(
    string Source,
    string? Target,
    string? Url,
    string? ApiKey,
    string? UnavailableReason = null,
    bool SelectedServerUnavailable = false);
