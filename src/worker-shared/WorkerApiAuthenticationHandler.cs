using Farm.Slicer.Module.Contracts;

namespace Farm.Slicer.Worker.Core;

/// <summary>
/// Names the HTTP client used exclusively for worker-only slicer API routes.
/// </summary>
public static class WorkerApiHttpClient
{
    /// <summary>The registered HTTP client name.</summary>
    public const string Name = "SlicerWorkerApi";
}

/// <summary>
/// Adds the current registry-issued worker identity to every worker-only API request.
/// </summary>
public sealed class WorkerApiAuthenticationHandler(IWorkerStateService workerState)
    : DelegatingHandler
{
    private readonly IWorkerStateService _workerState =
        workerState ?? throw new ArgumentNullException(nameof(workerState));

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        WorkerState state = _workerState.GetWorkerState();
        if (state.RegisteredServiceId is not { } serviceId ||
            string.IsNullOrWhiteSpace(state.RegisteredServiceApiKey))
        {
            throw new InvalidOperationException(
                "Authenticated worker identity is unavailable; refusing to send the API request.");
        }

        SetSingleHeader(request, WorkerLeaseHeaders.WorkerId, serviceId.ToString());
        SetSingleHeader(request, WorkerLeaseHeaders.WorkerKey, state.RegisteredServiceApiKey);
        return base.SendAsync(request, cancellationToken);
    }

    private static void SetSingleHeader(
        HttpRequestMessage request,
        string name,
        string value)
    {
        _ = request.Headers.Remove(name);
        if (!request.Headers.TryAddWithoutValidation(name, value))
        {
            throw new InvalidOperationException($"Unable to attach required worker header {name}.");
        }
    }
}
