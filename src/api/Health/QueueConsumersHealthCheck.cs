using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.Web.Api.Health;

/// <summary>
/// Reports whether the durable queue consumers and writers hosted by this API process are
/// actually running (issue #3157). Liveness is read from each hosted service's
/// <see cref="BackgroundService.ExecuteTask"/>: a consumer is running only when the host has
/// started it and its execute loop has not completed, faulted, or been cancelled. Every one of
/// these loops runs until the host stops, so a completed task always means the consumer is gone.
/// </summary>
/// <remarks>
/// This proves hosted-service liveness only. It does not inspect consumer throughput.
/// </remarks>
public sealed class QueueConsumersHealthCheck : IHealthCheck
{
    /// <summary>The stable <c>/health</c> results entry name.</summary>
    public const string Name = "queue-consumers";

    internal const string DisableBackgroundServicesKey = "TEST_DISABLE_BACKGROUND_SERVICES";

    internal const string Running = "running";
    internal const string NotRegistered = "notRegistered";
    internal const string NotStarted = "notStarted";
    internal const string Stopped = "stopped";
    internal const string Faulted = "faulted";
    internal const string Canceled = "canceled";
    internal const string Unobservable = "unobservable";
    internal const string Disabled = "disabled";

    /// <summary>
    /// The consumers this check requires, keyed by the camelCase name reported in the entry data.
    /// </summary>
    internal static readonly IReadOnlyList<KeyValuePair<string, Type>> DefaultConsumers =
    [
        new("autoDispatch", typeof(AutoDispatchBackgroundService)),
        new("queueOutboxPublisher", typeof(QueueOutboxPublisherService)),
        new("backendStartCommandConsumer", typeof(BackendStartCommandConsumerService)),
        new("backendControlCommandConsumer", typeof(BackendControlCommandConsumerService)),
        new("queueReconciliation", typeof(QueueReconciliationService)),
        new("queueRetentionPrune", typeof(QueueRetentionPruneService)),
        new("bedClearAcknowledgementExpiry", typeof(BedClearAcknowledgementExpiryService)),
    ];

    private readonly IEnumerable<IHostedService> _hostedServices;
    private readonly IConfiguration _configuration;
    private readonly IReadOnlyList<KeyValuePair<string, Type>> _consumers;

    public QueueConsumersHealthCheck(IEnumerable<IHostedService> hostedServices, IConfiguration configuration)
        : this(hostedServices, configuration, DefaultConsumers)
    {
    }

    internal QueueConsumersHealthCheck(
        IEnumerable<IHostedService> hostedServices,
        IConfiguration configuration,
        IReadOnlyList<KeyValuePair<string, Type>> consumers)
    {
        _hostedServices = hostedServices ?? throw new ArgumentNullException(nameof(hostedServices));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _consumers = consumers ?? throw new ArgumentNullException(nameof(consumers));
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        Dictionary<string, object> data = new(StringComparer.Ordinal);

        if (BackgroundServicesDisabled())
        {
            foreach (KeyValuePair<string, Type> consumer in _consumers)
            {
                data[consumer.Key] = Disabled;
            }

            return Task.FromResult(HealthCheckResult.Degraded(
                $"Queue consumers disabled by {DisableBackgroundServicesKey}",
                data: data));
        }

        List<IHostedService> hostedServices = _hostedServices.ToList();
        List<string> notRunning = [];
        foreach (KeyValuePair<string, Type> consumer in _consumers)
        {
            IHostedService? instance = hostedServices.FirstOrDefault(service => service.GetType() == consumer.Value);
            string state = Describe(instance);
            data[consumer.Key] = state;
            if (!string.Equals(state, Running, StringComparison.Ordinal))
            {
                notRunning.Add($"{consumer.Key}={state}");
            }
        }

        HealthCheckResult result = notRunning.Count == 0
            ? HealthCheckResult.Healthy($"All {_consumers.Count} queue consumers running", data)
            : HealthCheckResult.Unhealthy($"Queue consumers not running: {string.Join(", ", notRunning)}", data: data);
        return Task.FromResult(result);
    }

    internal static string Describe(IHostedService? service) => service switch
    {
        null => NotRegistered,
        BackgroundService { ExecuteTask: null } => NotStarted,
        BackgroundService { ExecuteTask.IsFaulted: true } => Faulted,
        BackgroundService { ExecuteTask.IsCanceled: true } => Canceled,
        BackgroundService { ExecuteTask.IsCompleted: true } => Stopped,
        BackgroundService => Running,
        _ => Unobservable,
    };

    private bool BackgroundServicesDisabled()
    {
        string? value = _configuration[DisableBackgroundServicesKey];
        return !string.IsNullOrEmpty(value)
            && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1");
    }
}
