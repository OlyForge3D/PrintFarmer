namespace Farm.OrcaSlicer.Worker.Health;

public class GracefulShutdownService(IWorkerStateService workerStateService, ILogger<GracefulShutdownService> logger, IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    private readonly CancellationTokenSource _shutdownTokenSource = new();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        applicationLifetime.ApplicationStopping.Register(OnShutdownRequested);
        logger.LogInformation("Graceful shutdown service started.");
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { logger.LogInformation("Graceful shutdown service stopping."); }
    }
    private void OnShutdownRequested()
    {
        logger.LogInformation("SIGTERM received. Initiating graceful shutdown...");
        workerStateService.SetShuttingDown();
        _ = Task.Run(async () =>
        {
            try { await PerformGracefulShutdownAsync(); }
            catch (Exception ex) { logger.LogError(ex, "Error during graceful shutdown"); }
            finally { await _shutdownTokenSource.CancelAsync(); }
        });
    }
    private async Task PerformGracefulShutdownAsync()
    {
        var maxWait = TimeSpan.FromSeconds(30);
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < maxWait)
        {
            var state = workerStateService.GetWorkerState();
            if (state.ActiveJobs == 0)
            {
                logger.LogInformation("All jobs completed. Proceeding with shutdown.");
                break;
            }
            logger.LogInformation("Waiting for {ActiveJobs} active jobs...", state.ActiveJobs);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        logger.LogInformation("Graceful shutdown completed.");
    }
    public override void Dispose()
    {
        _shutdownTokenSource.Dispose();
        base.Dispose();
    }
}
