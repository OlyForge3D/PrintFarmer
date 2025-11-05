using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Farm.Web.Api.Services.Workers;

/// <summary>
/// Validates worker API key headers against configured shared key.
/// </summary>
public interface IWorkerAuthService
{
    /// <summary>
    /// Returns true if the request contains a valid worker API key header.
    /// </summary>
    bool IsAuthorized(HttpContext httpContext);
}

public sealed class WorkerAuthService : IWorkerAuthService
{
    private readonly string? _sharedKey;
    private readonly IHostEnvironment _env;

    public const string HeaderName = "X-Worker-Key";

    public WorkerAuthService(IConfiguration configuration, IHostEnvironment env)
    {
        _env = env;
        // Hierarchy: explicit section, environment variable fallback WORKER_SHARED_API_KEY
        _sharedKey = configuration.GetSection(WorkerAuthSettings.SectionName)["SharedKey"]
                     ?? Environment.GetEnvironmentVariable("WORKER_SHARED_API_KEY");
    }

    public bool IsAuthorized(HttpContext httpContext)
    {
        if (httpContext == null)
        {
            return false;
        }
        // Allow bypass when no key configured and environment is Testing to keep integration tests simple until explicit key set.
        if (string.IsNullOrWhiteSpace(_sharedKey))
        {
            return _env.IsEnvironment("Testing");
        }
        if (!httpContext.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return false;
        }
        var presented = values.ToString();
        if (string.IsNullOrWhiteSpace(presented))
        {
            return false;
        }
        // Constant time compare minimal implementation (length + equality) – adequate for single shared key.
        if (presented.Length != _sharedKey.Length)
        {
            return false;
        }

        var equal = true;
        for (int i = 0; i < presented.Length; i++)
        {
            equal &= presented[i] == _sharedKey[i];
        }
        return equal;
    }
}
