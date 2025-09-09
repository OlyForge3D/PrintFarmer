namespace Farm.PrusaSlicer.Worker.Health;

/// <summary>
/// Background service that handles graceful shutdown on SIGTERM
/// </summary>
public class GracefulShutdownService(
    IWorkerStateService workerStateService,
    ILogger<GracefulShutdownService> logger,
    IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    private readonly CancellationTokenSource _shutdownTokenSource = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Register for shutdown notifications
        applicationLifetime.ApplicationStopping.Register(OnShutdownRequested);

        logger.LogInformation("Graceful shutdown service started. Worker ready to handle SIGTERM.");

        try
        {
            // Keep the service running until shutdown is requested
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
            logger.LogInformation("Graceful shutdown service stopping due to cancellation request.");
        }
    }

    private void OnShutdownRequested()
    {
        logger.LogInformation("SIGTERM received. Initiating graceful shutdown...");

        // Mark worker as shutting down so it won't accept new jobs
        workerStateService.SetShuttingDown();

        // Start shutdown process
        _ = Task.Run(async () =>
        {
            try
            {
                await PerformGracefulShutdownAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during graceful shutdown");
            }
            finally
            {
                await _shutdownTokenSource.CancelAsync();
            }
        });
    }

    private async Task PerformGracefulShutdownAsync()
    {
        logger.LogInformation("Starting graceful shutdown process...");

        var maxWaitTime = TimeSpan.FromSeconds(30); // Configurable shutdown timeout
        var checkInterval = TimeSpan.FromSeconds(1);
        var startTime = DateTime.UtcNow;

        // Wait for active jobs to complete or timeout
        while (DateTime.UtcNow - startTime < maxWaitTime)
        {
            var state = workerStateService.GetWorkerState();
            if (state.ActiveJobs == 0)
            {
                logger.LogInformation("All active jobs completed. Shutdown can proceed.");
                break;
            }

            logger.LogInformation("Waiting for {ActiveJobs} active jobs to complete. Time remaining: {TimeRemaining}s",
                state.ActiveJobs, (maxWaitTime - (DateTime.UtcNow - startTime)).TotalSeconds);

            await Task.Delay(checkInterval);
        }

        var finalState = workerStateService.GetWorkerState();
        if (finalState.ActiveJobs > 0)
        {
            logger.LogWarning("Shutdown timeout reached. {ActiveJobs} jobs may be interrupted.",
                finalState.ActiveJobs);
        }

        logger.LogInformation("Graceful shutdown completed.");
    }

    public override void Dispose()
    {
        _shutdownTokenSource?.Dispose();
        base.Dispose();
    }
}