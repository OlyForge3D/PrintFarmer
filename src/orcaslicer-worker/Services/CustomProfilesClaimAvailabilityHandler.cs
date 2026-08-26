using System.Net;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Prevents this worker from claiming new work while its custom profiles are
/// not synchronized. Existing leased-job traffic remains available.
/// </summary>
public sealed class CustomProfilesClaimAvailabilityHandler : DelegatingHandler
{
    private readonly CustomProfilesReconciliationState _state;
    private readonly Func<string> _getFingerprint;

    /// <summary>
    /// Initializes a new claim gate.
    /// </summary>
    /// <param name="state">Process reconciliation state.</param>
    /// <param name="bundleStore">Shared custom profile storage.</param>
    public CustomProfilesClaimAvailabilityHandler(
        CustomProfilesReconciliationState state,
        CustomProfileBundleStore bundleStore)
        : this(state, GetFingerprint(bundleStore))
    {
    }

    internal CustomProfilesClaimAvailabilityHandler(
        CustomProfilesReconciliationState state,
        Func<string> getFingerprint)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _getFingerprint = getFingerprint
            ?? throw new ArgumentNullException(nameof(getFingerprint));
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? path = request.RequestUri?.IsAbsoluteUri == true
            ? request.RequestUri.AbsolutePath
            : request.RequestUri?.OriginalString;
        bool isClaim = request.Method == HttpMethod.Post
            && string.Equals(
                path,
                "/api/slice/claim",
                StringComparison.OrdinalIgnoreCase);
        if (isClaim && !ProfilesAreCurrent())
        {
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request,
                    ReasonPhrase = "Custom profiles are not synchronized",
                });
        }

        return base.SendAsync(request, cancellationToken);
    }

    private static Func<string> GetFingerprint(
        CustomProfileBundleStore bundleStore)
    {
        ArgumentNullException.ThrowIfNull(bundleStore);
        return bundleStore.CalculateCustomProfilesFingerprint;
    }

    private bool ProfilesAreCurrent()
    {
        if (!_state.IsReady)
        {
            return false;
        }

        try
        {
            if (string.Equals(
                _getFingerprint(),
                _state.AppliedFingerprint,
                StringComparison.Ordinal))
            {
                return true;
            }
        }
        catch
        {
            // The reconciliation service records the detailed exception.
        }

        _state.MarkUnavailable(
            "Shared custom profiles changed; local reconciliation is pending.");
        return false;
    }
}
