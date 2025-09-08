using System.Runtime.InteropServices;
using System.Runtime.Loader; // Needed for AssemblyLoadContext

namespace Farm.OrcaSlicer.Worker.Health;

public class GracefulShutdownService : BackgroundService
{
    private readonly ILogger<GracefulShutdownService> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public GracefulShutdownService(ILogger<GracefulShutdownService> logger, IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _logger.LogInformation("ProcessExit received, shutting down gracefully");
        AssemblyLoadContext.Default.Unloading += _ => _logger.LogInformation("Unloading triggered, shutting down gracefully");
        Console.CancelKeyPress += (_, e) =>
        {
            _logger.LogInformation("CancelKeyPress received, initiating shutdown");
            e.Cancel = true;
            _lifetime.StopApplication();
        };
        return Task.CompletedTask;
    }
}
