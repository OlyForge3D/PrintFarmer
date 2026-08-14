using System.Net.WebSockets;
using Farm.Moonraker.Emulator.Domain;

namespace Farm.Moonraker.Emulator.Endpoints;

/// <summary>Request/response payloads for the <c>/__emulator/**</c> test-control API.</summary>
public sealed record ScenarioRequest(string Scenario);

public sealed record MmuModeRequest(string Mode);

public sealed record TimeAdvanceRequest(double Seconds);

public sealed record FaultRuleRequest(
    string? PrinterId,
    string Target,
    string Effect,
    string? PathContains,
    string? Method,
    string? RpcMethod,
    int? LatencyMs,
    int? HttpStatusCode,
    string? HttpBody,
    int? RpcErrorCode,
    string? RpcErrorMessage,
    double? StaleSeconds,
    bool Repeating,
    int RemainingUses = 1);

/// <summary>
/// Maps the deterministic test-control surface for this process's single printer:
/// scenario/reset control, explicit virtual-time advance/reset, and fault-injection
/// rule management. Only mapped when <c>Emulator:EnableControlApi=true</c>; otherwise
/// every <c>/__emulator/**</c> path falls through to the default 404.
/// </summary>
public static class ControlApiEndpoints
{
    public static IEndpointRouteBuilder MapEmulatorControlApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/__emulator");

        group.MapGet("/printer", (PrinterRegistry registry) => Results.Ok(PrinterSummary(registry.Printer)));

        // Root aliases some callers (React E2E fixtures/docs) use directly against the
        // control API root rather than the singular /printer sub-path. Kept alongside the
        // canonical singular routes below rather than replacing them, since this process
        // only ever emulates one printer either way — both simply read/mutate
        // registry.Printer. /__emulator/printers intentionally returns an array (the E2E
        // side parses it as Array<Record<...>>) even though it always has exactly one
        // element in this single-printer-per-process model.
        group.MapGet("/printers", (PrinterRegistry registry) => Results.Ok(new[] { PrinterSummary(registry.Printer) }));

        group.MapPost("/reset", async (PrinterRegistry registry) =>
        {
            PrinterAggregate printer = registry.Printer;
            string previousKlippyState = printer.KlippyState;
            MmuMode previousMmuMode = printer.Mmu.Mode;
            registry.ResetToInitial();
            await NotifyScenarioTransitionAsync(printer, previousKlippyState);
            await ReconnectIfObjectTopologyChangedAsync(printer, previousMmuMode);
            return Results.Ok(PrinterSummary(printer));
        });

        group.MapPost("/printer/scenario", async (ScenarioRequest request, PrinterRegistry registry) =>
        {
            if (!Enum.TryParse(request.Scenario, ignoreCase: true, out PrinterScenario scenario))
            {
                return Results.BadRequest(new { message = $"Unknown scenario '{request.Scenario}'." });
            }

            PrinterAggregate printer = registry.Printer;
            string previousKlippyState = printer.KlippyState;
            MmuMode previousMmuMode = printer.Mmu.Mode;
            registry.ResetToScenario(scenario);
            await NotifyScenarioTransitionAsync(printer, previousKlippyState);
            await ReconnectIfObjectTopologyChangedAsync(printer, previousMmuMode);
            return Results.Ok(PrinterSummary(printer));
        });

        group.MapPost("/printer/reset", async (PrinterRegistry registry) =>
        {
            PrinterAggregate printer = registry.Printer;
            string previousKlippyState = printer.KlippyState;
            MmuMode previousMmuMode = printer.Mmu.Mode;
            registry.ResetToInitial();
            await NotifyScenarioTransitionAsync(printer, previousKlippyState);
            await ReconnectIfObjectTopologyChangedAsync(printer, previousMmuMode);
            return Results.Ok(PrinterSummary(printer));
        });

        group.MapGet("/printer/mmu", (PrinterRegistry registry) => Results.Ok(MmuSummary(registry.Printer.Mmu)));

        group.MapPost("/printer/mmu", async (MmuModeRequest request, PrinterRegistry registry) =>
        {
            if (!Enum.TryParse(request.Mode, ignoreCase: true, out MmuMode mode))
            {
                return Results.BadRequest(new { message = $"Unknown MMU mode '{request.Mode}'. Supported modes: None, HappyHare, Afc, Qidibox, SnapmakerU1." });
            }

            PrinterAggregate printer = registry.Printer;
            MmuMode previousMmuMode = printer.Mmu.Mode;
            printer.SetMmuMode(mode);
            await BroadcastService.NotifyStatusUpdateAsync(printer);
            await ReconnectIfObjectTopologyChangedAsync(printer, previousMmuMode);
            return Results.Ok(MmuSummary(printer.Mmu));
        });

        group.MapGet("/time", (PrinterRegistry registry) =>
            Results.Ok(new { registry.Printer.Id, virtualTime = registry.Printer.Clock.UtcNow }));

        group.MapPost("/time/advance", async (TimeAdvanceRequest request, PrinterRegistry registry) =>
        {
            if (request.Seconds < 0)
            {
                return Results.BadRequest(new { message = "Seconds must be non-negative." });
            }

            PrinterAggregate printer = registry.Printer;
            printer.Clock.Advance(TimeSpan.FromSeconds(request.Seconds));
            printer.Tick();
            await BroadcastService.NotifyStatusUpdateAsync(printer);

            return Results.Ok(new { printer.Id, virtualTime = printer.Clock.UtcNow, printer.PrintState });
        });

        group.MapPost("/time/reset", async (PrinterRegistry registry) =>
        {
            PrinterAggregate printer = registry.Printer;
            printer.Clock.Reset();
            printer.Tick();
            await BroadcastService.NotifyStatusUpdateAsync(printer);
            return Results.Ok(new { printer.Id, virtualTime = printer.Clock.UtcNow });
        });

        group.MapGet("/rules", (PrinterRegistry registry) => Results.Ok(registry.Rules.List().Select(RuleDto)));

        group.MapPost("/rules", (FaultRuleRequest request, PrinterRegistry registry) =>
        {
            if (!Enum.TryParse(request.Target, ignoreCase: true, out FaultTarget target))
            {
                return Results.BadRequest(new { message = $"Unknown target '{request.Target}'. Expected Http or WebSocket." });
            }

            if (!Enum.TryParse(request.Effect, ignoreCase: true, out FaultEffect effect))
            {
                return Results.BadRequest(new { message = $"Unknown effect '{request.Effect}'." });
            }

            if (effect == FaultEffect.Latency && request.LatencyMs is not > 0)
            {
                return Results.BadRequest(new { message = "Latency effect requires a positive latencyMs." });
            }

            if (effect == FaultEffect.HttpStatus && request.HttpStatusCode is null)
            {
                return Results.BadRequest(new { message = "HttpStatus effect requires httpStatusCode." });
            }

            if (effect == FaultEffect.RpcError && request.RpcErrorCode is null)
            {
                return Results.BadRequest(new { message = "RpcError effect requires rpcErrorCode." });
            }

            if (request.RemainingUses < 1 && !request.Repeating)
            {
                return Results.BadRequest(new { message = "remainingUses must be at least 1 for a one-shot rule." });
            }

            var rule = new FaultRule
            {
                PrinterId = request.PrinterId,
                Target = target,
                Effect = effect,
                PathContains = request.PathContains,
                Method = request.Method,
                RpcMethod = request.RpcMethod,
                LatencyMs = request.LatencyMs,
                HttpStatusCode = request.HttpStatusCode,
                HttpBody = request.HttpBody,
                RpcErrorCode = request.RpcErrorCode,
                RpcErrorMessage = request.RpcErrorMessage,
                StaleSeconds = request.StaleSeconds,
                Repeating = request.Repeating,
                RemainingUses = request.RemainingUses,
            };
            registry.Rules.Add(rule);
            return Results.Ok(RuleDto(rule));
        });

        group.MapDelete("/rules/{id}", (string id, PrinterRegistry registry) =>
            registry.Rules.Remove(id) ? Results.Ok() : Results.NotFound());

        group.MapPost("/rules/clear", (PrinterRegistry registry) =>
        {
            registry.Rules.Clear();
            return Results.Ok();
        });

        return app;
    }

    private static Task NotifyScenarioTransitionAsync(PrinterAggregate printer, string previousKlippyState) =>
        BroadcastService.NotifyKlippyTransitionIfChangedAsync(printer, previousKlippyState);

    private static Task ReconnectIfObjectTopologyChangedAsync(PrinterAggregate printer, MmuMode previousMmuMode) =>
        previousMmuMode == printer.Mmu.Mode
            ? Task.CompletedTask
            : ReconnectSubscribersForObjectDiscoveryAsync(printer);

    private static async Task ReconnectSubscribersForObjectDiscoveryAsync(PrinterAggregate printer)
    {
        foreach (WsSubscription subscription in printer.Connections.Values.ToArray())
        {
            if (subscription.Socket.State is not WebSocketState.Open)
            {
                continue;
            }

            await subscription.Socket.CloseOutputAsync(
                WebSocketCloseStatus.EndpointUnavailable,
                "Emulator fixture topology changed; reconnect to rediscover objects.",
                CancellationToken.None);
        }
    }

    private static object PrinterSummary(PrinterAggregate p) => new
    {
        p.Id,
        p.Name,
        p.KlippyState,
        p.PrintState,
        p.Filename,
        progress = p.Progress(),
        virtualTime = p.Clock.UtcNow,
        connections = p.Connections.Count,
    };

    private static object MmuSummary(MmuFixture mmu) => new
    {
        mode = mmu.Mode.ToString(),
        mmu.Detected,
        mmu.NumGates,
    };

    private static object RuleDto(FaultRule r) => new
    {
        r.Id,
        r.PrinterId,
        Target = r.Target.ToString(),
        Effect = r.Effect.ToString(),
        r.PathContains,
        r.Method,
        r.RpcMethod,
        r.LatencyMs,
        r.HttpStatusCode,
        r.HttpBody,
        r.RpcErrorCode,
        r.RpcErrorMessage,
        r.StaleSeconds,
        r.Repeating,
        r.RemainingUses,
    };
}
