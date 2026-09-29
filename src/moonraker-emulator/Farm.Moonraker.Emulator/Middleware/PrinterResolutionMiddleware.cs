using System.Text.Json;
using Farm.Moonraker.Emulator.Domain;
using Farm.Moonraker.Emulator.Json;
using Farm.Moonraker.Emulator.Options;
using Microsoft.Extensions.Options;

namespace Farm.Moonraker.Emulator.Middleware;

/// <summary>
/// Attaches this process's single <see cref="PrinterAggregate"/> to every request and
/// applies any matching HTTP fault-injection rule before the request reaches its
/// endpoint. There is no per-request printer dispatch (no Host-header lookup, no
/// <c>/printers/{id}</c> path prefix) — one emulator process always answers as the one
/// printer it was configured for via <c>Emulator:Scenario</c> /
/// <c>Emulator:PrinterId</c> / <c>Emulator:PrinterName</c>. Multiple printer scenarios
/// are represented by running multiple isolated instances (e.g. separate Compose
/// services), matching how <c>MoonrakerClient</c> always resolves routes as relative
/// URIs against a printer's root <c>BackendUrl</c>.
/// </summary>
public sealed class PrinterResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, PrinterRegistry registry, IOptions<EmulatorOptions> options)
    {
        PrinterAggregate printer = registry.Printer;
        context.Items["printer"] = printer;

        string path = context.Request.Path.Value ?? "/";

        // The /__emulator/** control surface is meta/test-control, not emulated
        // Moonraker protocol traffic — fault rules must never apply to it, or a broad
        // rule (e.g. "500 for every request") could brick the very control API needed
        // to clear that rule again.
        bool isControlApiRequest = path.StartsWith("/__emulator", StringComparison.OrdinalIgnoreCase);
        bool isHealthRequest = path.Equals("/healthz", StringComparison.OrdinalIgnoreCase);
        if (!isControlApiRequest && !isHealthRequest)
        {
            // Recorded before auth/fault handling so a rejected or faulted command
            // attempt is still visible as an attempt.
            string? proxiedMethod = await ReadSpoolmanProxyMethodAsync(context, path);
            context.Items["requestLogEntry"] = registry.Requests.RecordHttp(context.Request.Method, path, proxiedMethod);
        }

        string? requiredApiKey = options.Value.ApiKey;
        if (!isControlApiRequest &&
            !isHealthRequest &&
            !string.IsNullOrEmpty(requiredApiKey) &&
            !string.Equals(context.Request.Headers["X-Api-Key"], requiredApiKey, StringComparison.Ordinal))
        {
            await MoonrakerJson.WriteWebRequestErrorAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized");
            return;
        }

        FaultRule? rule = isControlApiRequest
            ? null
            : registry.Rules.MatchHttp(printer.Id, context.Request.Method, path);
        if (rule is not null)
        {
            if (rule.LatencyMs is > 0)
            {
                await Task.Delay(rule.LatencyMs.Value, context.RequestAborted);
            }

            switch (rule.Effect)
            {
                case FaultEffect.HttpStatus:
                    context.Response.StatusCode = rule.HttpStatusCode ?? StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(rule.HttpBody ?? "{}");
                    return;
                case FaultEffect.KlippyUnavailable:
                    await MoonrakerJson.WriteWebRequestErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "Klippy is not connected");
                    return;
                case FaultEffect.MalformedJson:
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{ this is not valid json ");
                    return;
                case FaultEffect.Latency:
                    break; // already applied above
                case FaultEffect.RpcError:
                case FaultEffect.WsDisconnect:
                case FaultEffect.StaleNotifications:
                default:
                    break; // WebSocket-only effects do not apply to REST requests
            }
        }

        await next(context);
    }

    private const int MaxProxyBodyBytes = 64 * 1024;

    // Peeks the forwarded request_method of a Spoolman proxy call without consuming the body
    // the endpoint reads next. Any other request, or an unreadable body, returns null.
    private static async Task<string?> ReadSpoolmanProxyMethodAsync(HttpContext context, string path)
    {
        if (!HttpMethods.IsPost(context.Request.Method) ||
            !path.TrimEnd('/').Equals(RequestLog.SpoolmanProxyPath, StringComparison.OrdinalIgnoreCase) ||
            context.Request.ContentLength is > MaxProxyBodyBytes)
        {
            return null;
        }

        context.Request.EnableBuffering(MaxProxyBodyBytes);
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("request_method", out JsonElement method) &&
                   method.ValueKind == JsonValueKind.String
                ? method.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            context.Request.Body.Position = 0;
        }
    }
}
