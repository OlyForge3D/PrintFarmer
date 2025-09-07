using System.Net.Http.Headers;

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
        if (!Uri.TryCreate(devServerUrl.TrimEnd('/'), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Invalid SPA dev server URL", nameof(devServerUrl));
        }
        DevServerUrl = uri;
    }
}

/// <summary>
/// Background watcher that periodically probes the SPA dev server and activates proxying when reachable.
/// </summary>
public sealed class SpaDevServerWatcher : BackgroundService
{
    private readonly SpaProxyActivationState _state;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SpaDevServerWatcher> _logger;
    private readonly int _intervalMs;

    public SpaDevServerWatcher(SpaProxyActivationState state, IHttpClientFactory httpClientFactory, ILogger<SpaDevServerWatcher> logger, IConfiguration config)
    {
        _state = state;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _intervalMs = config.GetValue<int?>("SPA_PROXY_POLL_INTERVAL_MS") ?? 1500;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Poll until active or cancelled
        while (!stoppingToken.IsCancellationRequested && !_state.Active)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("SpaProxy");
                client.Timeout = TimeSpan.FromMilliseconds(750);
                var resp = await client.GetAsync(_state.DevServerUrl, stoppingToken);
                if (resp.IsSuccessStatusCode)
                {
                    _state.Activate();
                    _logger.LogInformation("[SPA] Dev server detected at {Url}; proxy activation enabled", _state.DevServerUrl);
                    break;
                }
                else
                {
                    _logger.LogDebug("[SPA] Probe status {Status} for {Url}", (int)resp.StatusCode, _state.DevServerUrl);
                }
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "[SPA] Probe failed for {Url}", _state.DevServerUrl);
            }
            await Task.Delay(_intervalMs, stoppingToken);
        }
    }
}

/// <summary>
/// Middleware that proxies unknown GET/HEAD requests to the dev server after activation.
/// </summary>
public sealed class SpaDynamicProxyMiddleware
{
    private static readonly HttpClient s_client = new();
    private readonly RequestDelegate _next;
    private readonly SpaProxyActivationState _state;
    private readonly ILogger<SpaDynamicProxyMiddleware> _logger;

    public SpaDynamicProxyMiddleware(RequestDelegate next, SpaProxyActivationState state, ILogger<SpaDynamicProxyMiddleware> logger)
    {
        _next = next;
        _state = state;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only proxy when activated, only for root-like SPA routes, and only for GET/HEAD
        ArgumentNullException.ThrowIfNull(context);
        if (_state.Active && HttpMethods.IsGet(context.Request.Method) && !IsApiOrStatic(context.Request.Path))
        {
            var target = new Uri(_state.DevServerUrl, context.Request.Path + context.Request.QueryString);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, target);
                CopyHeaders(context, req);
                var resp = await s_client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                if (IsHtml(resp))
                {
                    context.Response.StatusCode = (int)resp.StatusCode;
                    foreach (var h in resp.Headers)
                    {
                        context.Response.Headers[h.Key] = h.Value.ToArray();
                    }
                    foreach (var h in resp.Content.Headers)
                    {
                        context.Response.Headers[h.Key] = h.Value.ToArray();
                    }
                    // Prevent double encoding
                    context.Response.Headers.Remove("transfer-encoding");
                    await resp.Content.CopyToAsync(context.Response.Body);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SPA] Proxy failure to {Target}", target);
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
        foreach (var header in ctx.Request.Headers)
        {
            // Skip certain hop-by-hop headers
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            req.Headers.TryAddWithoutValidation(header.Key, header.Value.AsEnumerable());
        }
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
    }
}
