using Farm.Slicer.Worker.Core;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Background service that handles worker registration, periodic heartbeats, and deregistration.
/// Waits for the profile cache to be ready before registering with the API.
/// </summary>
public class RegistrationBackgroundService : BackgroundService
{
    private readonly ISlicerRegistrationClient _registrationClient;
    private readonly IWorkerStateService _workerState;
    private readonly CachedOrcaProfilesService? _cachedProfilesService;
    private readonly CustomProfilesReconciliationState _customProfilesState;
    private readonly ILogger<RegistrationBackgroundService> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    private Guid _serviceId;
    private string _apiKey = string.Empty;
    private bool _isRegistered;
    private readonly int _heartbeatIntervalSeconds;
    private readonly int _maxConcurrentJobs;

    public RegistrationBackgroundService(
        ISlicerRegistrationClient registrationClient,
        IWorkerStateService workerState,
        ISlicerProfilesService profilesService,
        CustomProfilesReconciliationState customProfilesState,
        IConfiguration configuration,
        ILogger<RegistrationBackgroundService> logger,
        IHostApplicationLifetime lifetime)
    {
        _registrationClient = registrationClient ?? throw new ArgumentNullException(nameof(registrationClient));
        _workerState = workerState ?? throw new ArgumentNullException(nameof(workerState));

        // Get the cached service if available (for waiting on cache readiness)
        _cachedProfilesService = profilesService as CachedOrcaProfilesService;
        _customProfilesState = customProfilesState
            ?? throw new ArgumentNullException(nameof(customProfilesState));
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));

        _heartbeatIntervalSeconds = configuration.GetValue("SlicerRegistry:HeartbeatIntervalSeconds", 30);
        _maxConcurrentJobs = configuration.GetValue("Worker:MaxConcurrentJobs", 1);

        // Register for shutdown to deregister cleanly
        _ = _lifetime.ApplicationStopping.Register(OnShutdown);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RegistrationBackgroundService starting...");

        // Wait for the profile cache to be ready before registering
        if (_cachedProfilesService != null)
        {
            _logger.LogInformation("Waiting for profile cache to be ready before registering...");
            try
            {
                // Wait for cache with a timeout (5 minutes should be enough for even slow systems)
                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
                await _cachedProfilesService.CacheReadyTask.WaitAsync(timeoutCts.Token);
                _logger.LogInformation("Profile cache is ready, proceeding with registration.");
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError("Timed out waiting for profile cache. Will attempt registration anyway.");
            }
        }
        else
        {
            // No cached service, wait a bit for the worker to fully initialize
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        // Initial registration
        if (!await TryRegisterAsync(stoppingToken))
        {
            _logger.LogWarning("Initial registration failed. Will retry on next heartbeat cycle.");
        }

        // Heartbeat loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_heartbeatIntervalSeconds), stoppingToken);

                // If not registered, try to register again
                if (!_isRegistered)
                {
                    _ = await TryRegisterAsync(stoppingToken);
                    continue;
                }

                // Send heartbeat with current capacity
                WorkerState state = _workerState.GetWorkerState();
                (int freeSlots, string status) =
                    CalculateHeartbeatAvailability(
                        state,
                        _maxConcurrentJobs,
                        _customProfilesState.IsReady);

                SlicerHeartbeatResult heartbeatResult = await _registrationClient.HeartbeatAsync(
                    _serviceId,
                    _apiKey,
                    freeSlots,
                    status,
                    stoppingToken);

                if (heartbeatResult == SlicerHeartbeatResult.ReRegister)
                {
                    _logger.LogWarning("Registered identity is no longer accepted. Re-registering on the next cycle.");
                    _isRegistered = false;
                    _workerState.ClearRegisteredService();
                }
                else if (heartbeatResult == SlicerHeartbeatResult.Retry)
                {
                    _logger.LogWarning("Heartbeat failed transiently. Retaining the current registered identity.");
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in heartbeat loop");

                // Continue trying
            }
        }

        _logger.LogInformation("RegistrationBackgroundService stopped.");
    }

    internal static (int FreeSlots, string Status)
        CalculateHeartbeatAvailability(
            WorkerState state,
            int maxConcurrentJobs,
            bool customProfilesReady)
    {
        ArgumentNullException.ThrowIfNull(state);
        int freeSlots = customProfilesReady
            ? Math.Max(0, maxConcurrentJobs - state.ActiveJobs)
            : 0;
        string status = state.IsShuttingDown
            ? "Draining"
            : customProfilesReady
                ? "Online"
                : "Error";
        return (freeSlots, status);
    }

    private async Task<bool> TryRegisterAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_customProfilesState.IsReady)
            {
                string failure = _customProfilesState.Failure
                    ?? "initial reconciliation is pending";
                _logger.LogWarning(
                    "Registration deferred because custom profiles are not synchronized: {Failure}",
                    failure);
                return false;
            }

            _logger.LogInformation("Attempting to register with slicer registry...");

            (Guid serviceId, string apiKey) = await _registrationClient.RegisterAsync(cancellationToken);

            _serviceId = serviceId;
            _apiKey = apiKey;
            _isRegistered = true;
            _workerState.SetRegisteredService(serviceId, apiKey);

            _logger.LogInformation(
                "Successfully registered with slicer registry. ServiceId: {ServiceId}, HeartbeatInterval: {Interval}s",
                _serviceId,
                _heartbeatIntervalSeconds);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register with slicer registry");
            _isRegistered = false;
            _workerState.ClearRegisteredService();
            return false;
        }
    }

    private void OnShutdown()
    {
        if (!_isRegistered)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Application shutting down, deregistering from slicer registry...");

            // Use a short timeout for deregistration
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task<bool> deregisterTask = _registrationClient.DeregisterAsync(_serviceId, _apiKey, cts.Token);

            // Block until deregistration completes or times out
#pragma warning disable VSTHRD002 // Synchronous wait acceptable in shutdown scenario
            deregisterTask.Wait(cts.Token);

            if (deregisterTask.Result)
            {
                _workerState.ClearRegisteredService();
                _logger.LogInformation("Successfully deregistered from slicer registry.");
            }
            else
            {
                _logger.LogWarning("Deregistration returned false.");
            }
#pragma warning restore VSTHRD002 // Synchronous wait acceptable in shutdown scenario
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deregister from slicer registry during shutdown");
        }
    }
}
