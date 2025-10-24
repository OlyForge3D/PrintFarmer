// Legacy Redis-based queue consumer was removed as part of the HTTP claim/complete migration.
// Keep a small stub to preserve public API surface for projects that still reference the type.
// TODO: Remove this file completely once all references are updated to use HttpJobPollerService.

using Microsoft.Extensions.Hosting;

namespace Farm.Slicer.Worker.Core;

public abstract class BaseQueueConsumerService : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No-op: legacy consumer removed. If you reach here, switch to HttpJobPollerService.
        return Task.CompletedTask;
    }
}
