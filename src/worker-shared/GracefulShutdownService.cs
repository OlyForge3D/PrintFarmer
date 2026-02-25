using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Worker.Core;

public class GracefulShutdownService : BackgroundService
{
    private readonly IWorkerStateService _state;
    private readonly ILogger<GracefulShutdownService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeSpan _grace;
    private readonly CancellationTokenSource _cts = new();

    public GracefulShutdownService(IWorkerStateService state, ILogger<GracefulShutdownService> logger, IHostApplicationLifetime lifetime, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(config);
        _state = state;
        _logger = logger;
        _lifetime = lifetime;
        int seconds = int.TryParse(config["Worker:Shutdown:GraceSeconds"], out int s) ? s : 30;
        _grace = TimeSpan.FromSeconds(seconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = _lifetime.ApplicationStopping.Register(OnStopping);
        _logger.LogInformation("Graceful shutdown service active (grace={TotalSeconds}s)", _grace.TotalSeconds);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown path
        }
    }

    private void OnStopping()
    {
        _logger.LogInformation("Termination requested; entering graceful shutdown window ({TotalSeconds}s)", _grace.TotalSeconds);
        _state.SetShuttingDown();
        _ = Task.Run(async () =>
        {
            DateTime start = DateTime.UtcNow;
            while (DateTime.UtcNow - start < _grace)
            {
                WorkerState snapshot = _state.GetWorkerState();
                if (snapshot.ActiveJobs == 0)
                {
                    _logger.LogInformation("All jobs complete after {TotalMilliseconds}ms", (DateTime.UtcNow - start).TotalMilliseconds);
                    break;
                }

                _logger.LogInformation("Waiting on {SnapshotActiveJobs} active jobs... {TotalSeconds}s left", snapshot.ActiveJobs, (_grace - (DateTime.UtcNow - start)).TotalSeconds);
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            await _cts.CancelAsync();
        });
    }

    public override void Dispose()
    {
        _cts.Dispose();
        base.Dispose();
    }
}
