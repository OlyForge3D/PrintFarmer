using System.Collections.Concurrent;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Owns physical sends independently of HTTP lifetimes. Never replays a committed send.</summary>
public sealed class PrinterControlOperationWorker(
    IServiceScopeFactory scopes,
    ILogger<PrinterControlOperationWorker> logger) : BackgroundService
{
    private readonly Guid owner = Guid.NewGuid();
    private readonly ConcurrentDictionary<Guid, Execution> active = new();
    private readonly ConcurrentDictionary<Guid, byte> quiesced = new();
    private volatile bool stopAdmissions;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        stopAdmissions = true;
        try
        {
            // Give already-sent work the host's shutdown grace period to return its exact
            // response. This is host lifetime management, not a physical command deadline.
            await Task.WhenAll(active.Values.Select(execution => execution.Task)).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await base.StopAsync(cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // Exception messages/objects can contain connection credentials or backend payloads.
                    logger.LogWarning(
                        "Motion control scan failed ({ExceptionType}); persisted barriers remain held.",
                        exception.GetType().Name);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Host cancellation stops sends and joins I/O. A sent operation whose response
            // cannot be collected is persisted Unknown, not returned to the work queue.
            foreach (Execution execution in active.Values)
            {
                await execution.Stop.CancelAsync();
            }

            await Task.WhenAll(active.Values.Select(e => e.Task));
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        foreach ((Guid id, Execution execution) in active)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PrinterControlOperation? operation = await db.PrinterControlOperations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, ct);
            if (operation is null || operation.OwnerToken != owner || operation.State is PrinterControlState.Recovering or PrinterControlState.Recovered)
            {
                await execution.Stop.CancelAsync();
            }

            if (execution.Task.IsCompleted)
            {
                await execution.Task;
                active.TryRemove(id, out _);
                execution.Stop.Dispose();
            }
            else
            {
                await scope.ServiceProvider.GetRequiredService<PrinterControlOperationService>().MaintainOwnerAsync(id, owner, false, ct);
            }
        }

        await using AsyncServiceScope scan = scopes.CreateAsyncScope();
        AppDbContext scanDb = scan.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterControlOperationService service = scan.ServiceProvider.GetRequiredService<PrinterControlOperationService>();
        Guid[] recoveries = await scanDb.PrinterControlOperations.AsNoTracking()
            .Where(o => o.State == PrinterControlState.Recovering && o.OwnerToken == owner &&
                o.SenderIsolation != PrinterSenderIsolation.Confirmed)
            .Select(o => o.Id).ToArrayAsync(ct);
        foreach (Guid id in recoveries)
        {
            if (quiesced.ContainsKey(id))
            {
                await service.MaintainOwnerAsync(id, owner, true, ct);
            }
        }

        await service.ReconcileOrphansAsync(ct);
        Guid[] legacy = await scanDb.PrinterDispatchStates.AsNoTracking().Where(s =>
            s.PhysicalControlCommandId != null && s.PhysicalControlAttemptId == null &&
            (s.PhysicalControlOperation == "home" || s.PhysicalControlOperation == "home_xy" ||
             s.PhysicalControlOperation == "home_z" || s.PhysicalControlOperation == "move" || s.PhysicalControlOperation == "move_to"))
            .Select(s => s.PrinterId).ToArrayAsync(ct);
        foreach (Guid id in legacy)
        {
            await service.ImportLegacyAsync(id, ct);
        }

        if (stopAdmissions)
        {
            return;
        }

        DateTime cutoff = DateTime.UtcNow - PrinterControlOperationService.OwnerLiveness;
        Guid[] queued = await scanDb.PrinterControlOperations.AsNoTracking().Where(o =>
            o.State == PrinterControlState.Queued && o.SendCommittedAtUtc == null &&
            (o.OwnerToken == null || o.OwnerHeartbeatAtUtc == null || o.OwnerHeartbeatAtUtc < cutoff))
            .OrderBy(o => o.CreatedAtUtc).Take(20).Select(o => o.Id).ToArrayAsync(ct);
        foreach (Guid id in queued)
        {
            var execution = new Execution();
            if (active.TryAdd(id, execution))
            {
                execution.Task = RunOneAsync(id, execution.Stop.Token);
            }
            else
            {
                execution.Stop.Dispose();
            }
        }
    }

    private async Task RunOneAsync(Guid id, CancellationToken ct)
    {
        bool claimed = false;
        bool success = false;
        string? failure = null;
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            PrinterControlOperationService service = scope.ServiceProvider.GetRequiredService<PrinterControlOperationService>();
            PrinterControlOperation? operation = await service.ClaimAsync(id, owner, ct);
            if (operation is null)
            {
                return;
            }

            claimed = true;
            await service.AuthorizeActorAsync(operation.PrinterId, operation.ActorSubject, ct);
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Printer printer = await db.Printers.AsNoTracking().SingleAsync(p => p.Id == operation.PrinterId, ct);
            if (PrinterControlIntent.ConfigurationIdentity(printer) != operation.PrinterConfigurationIdentity)
            {
                throw new PrinterControlException(409, "printer_configuration_changed", "Printer configuration changed.");
            }

            IMoonrakerMotionChannelFactory channels = scope.ServiceProvider.GetService<IMoonrakerMotionChannelFactory>()
                ?? throw new PrinterControlException(422, "printer_operation_unsupported", "The Moonraker motion plugin is unavailable.");
            await using IMoonrakerMotionChannel channel = await channels.ConnectAsync(printer, ct);
            if (!await channel.IsIdleAsync(ct))
            {
                throw new PrinterControlException(409, "printer_busy", "The backend is not ready and idle.");
            }

            var intent = new PrinterControlRequest(operation.Kind, operation.X, operation.Y, operation.Z, operation.F);
            if (operation.Kind is PrinterControlKind.MoveTo or PrinterControlKind.Jog)
            {
                PrinterStatusDto observed = await channel.ReadMotionStateAsync(operation.PrinterId, ct);
                var target = operation.Kind == PrinterControlKind.Jog
                    ? new PrinterSafetyMoveRequest(observed.X + (operation.X ?? 0), observed.Y + (operation.Y ?? 0), observed.Z + (operation.Z ?? 0))
                    : new PrinterSafetyMoveRequest(operation.X, operation.Y, operation.Z);
                PrinterSafetyValidationResult safety = await scope.ServiceProvider.GetRequiredService<IPrinterSafetyGuard>()
                    .ValidateObservedMoveAsync(operation.PrinterId, target, observed, ct);
                if (!safety.Success)
                {
                    throw new PrinterControlException(safety.StatusCode, safety.Code ?? "unsafe_motion", "Motion safety validation failed.");
                }
            }

            if (!await service.CommitSendAsync(id, owner, ct))
            {
                return;
            }

            // No request token, retry policy, elapsed-motion timeout or lifecycle consumer.
            await channel.ExecuteAsync(id, PrinterControlIntent.BuildScript(intent), ct);
            success = true;
        }
        catch (PrinterControlException exception)
        {
            failure = exception.Code;
        }
        catch (OperationCanceledException)
        {
            failure = "sender_interrupted";
        }
        catch (Exception exception)
        {
            failure = "backend_outcome_unknown";
            logger.LogWarning(
                "Motion operation {OperationId} raised {ExceptionType}; persisting outcome without replay.",
                id, exception.GetType().Name);
        }
        finally
        {
            if (claimed)
            {
                quiesced.TryAdd(id, 0);

                // The channel's await-using has now aborted/disposed and joined its I/O.
                // Retry only the durable outcome write, never the physical request.
                await PersistResultAsync(id, success, failure);
            }
        }
    }

    private async Task PersistResultAsync(Guid id, bool success, string? failure)
    {
        const int maxAttempts = 12;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                PrinterControlOperationService service = scope.ServiceProvider.GetRequiredService<PrinterControlOperationService>();
                await service.SetOutcomeAsync(id, owner, success, failure, CancellationToken.None);
                await service.MaintainOwnerAsync(id, owner, isolated: true, CancellationToken.None);
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (await db.PrinterControlOperations.AnyAsync(operation => operation.Id == id &&
                    (operation.State == PrinterControlState.Succeeded || operation.State == PrinterControlState.Failed ||
                     operation.State == PrinterControlState.Recovered)))
                {
                    quiesced.TryRemove(id, out _);
                }

                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Could not persist motion outcome {OperationId} on attempt {Attempt} of {MaxAttempts} ({ExceptionType}); retrying persistence only.",
                    id, attempt + 1, maxAttempts, exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        // Owner liveness will expose an unresolved sent operation as Unknown. Never send again.
        logger.LogError("Motion result {OperationId} remains unpersisted; durable barrier retained.", id);
    }

    private sealed class Execution
    {
        public CancellationTokenSource Stop { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;
    }
}
