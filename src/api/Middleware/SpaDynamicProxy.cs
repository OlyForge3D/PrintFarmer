using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Middleware;

public sealed class SpaProxyActivationState
{
    private volatile bool _active;

    public Uri DevServerUrl { get; }

    public bool Active => _active;

    public void Activate() => _active = true;

    public SpaProxyActivationState(string devServerUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devServerUrl);
        if (!Uri.TryCreate(devServerUrl.TrimEnd('/'), UriKind.Absolute, out Uri? uri))
        {
            throw new ArgumentException("Invalid SPA dev server URL", nameof(devServerUrl));
        }

        DevServerUrl = uri;
    }
}

/// <summary>
/// Background watcher that periodically probes the SPA dev server and activates proxying when reachable.
/// </summary>
public sealed class SpaDevServerWatcher(SpaProxyActivationState state, IHttpClientFactory httpClientFactory, IConfiguration config) : BackgroundService
{
    private readonly SpaProxyActivationState _state = state;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly int _intervalMs = config.GetValue<int?>("SPA_PROXY_POLL_INTERVAL_MS") ?? 1500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Poll until active or cancelled
        while (!stoppingToken.IsCancellationRequested && !_state.Active)
        {
            try
            {
                HttpClient client = _httpClientFactory.CreateClient("SpaProxy");
                client.Timeout = TimeSpan.FromMilliseconds(750);
                HttpResponseMessage resp = await client.GetAsync(_state.DevServerUrl, stoppingToken);
                if (resp.IsSuccessStatusCode)
                {
                    _state.Activate();

                    // Logging moved to method injection if needed
                    break;
                }
                else
                {
                    // Logging moved to method injection if needed
                }
            }
            catch (Exception)
            {
                // Logging moved to method injection if needed
            }

            await Task.Delay(_intervalMs, stoppingToken);
        }
    }
}

/// <summary>
/// Middleware that proxies unknown GET/HEAD requests to the dev server after activation.
/// </summary>
public sealed class SpaDynamicProxyMiddleware(RequestDelegate next, SpaProxyActivationState state)
{
    private static readonly HttpClient s_client = new();
    private readonly RequestDelegate _next = next;
    private readonly SpaProxyActivationState _state = state;

    public async Task InvokeAsync(HttpContext context, [FromServices] ILogger<SpaProxyActivationState> logger)
    {
        // Only proxy when activated, only for root-like SPA routes, and only for GET/HEAD
        ArgumentNullException.ThrowIfNull(context);
        if (_state.Active && HttpMethods.IsGet(context.Request.Method) && !IsApiOrStatic(context.Request.Path))
        {
            Uri target = new(_state.DevServerUrl, context.Request.Path + context.Request.QueryString);
            try
            {
                using HttpRequestMessage req = new(HttpMethod.Get, target);
                CopyHeaders(context, req);

                // Propagate correlationId header if present
                string? correlationId = context.Items["CorrelationId"] as string ?? context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(correlationId))
                {
                    _ = req.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
                }

                HttpResponseMessage resp = await s_client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                if (IsHtml(resp))
                {
                    context.Response.StatusCode = (int)resp.StatusCode;
                    foreach (KeyValuePair<string, IEnumerable<string>> h in resp.Headers)
                    {
                        context.Response.Headers[h.Key] = h.Value.ToArray();
                    }

                    foreach (KeyValuePair<string, IEnumerable<string>> h in resp.Content.Headers)
                    {
                        context.Response.Headers[h.Key] = h.Value.ToArray();
                    }

                    // Prevent double encoding
                    _ = context.Response.Headers.Remove("transfer-encoding");
                    await resp.Content.CopyToAsync(context.Response.Body);
                    return;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, $"[SPA] Proxy failure to {target}", null, null);
            }
        }

        await _next(context);
    }

    private static bool IsApiOrStatic(PathString path)
        => path.StartsWithSegments("/api")
           || path.StartsWithSegments("/hubs")
           || path.StartsWithSegments("/health")
           || path.StartsWithSegments("/healthz")
           || (path.Value?.Contains('.') ?? false);

    private static bool IsHtml(HttpResponseMessage resp)
        => resp.Content.Headers.ContentType?.MediaType == "text/html";

    private static void CopyHeaders(HttpContext ctx, HttpRequestMessage req)
    {
        foreach (KeyValuePair<string, StringValues> header in ctx.Request.Headers)
        {
            // Skip certain hop-by-hop headers
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _ = req.Headers.TryAddWithoutValidation(header.Key, header.Value.AsEnumerable());
        }

        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
    }
}
