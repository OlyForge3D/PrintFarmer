using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace Farm.Slicer.Module.Api.Services;

public sealed class WorkerAuthService(IConfiguration configuration, IHostEnvironment env) : IWorkerAuthService
{
    private readonly string? _sharedKey = configuration.GetSection(WorkerAuthSettings.SectionName)["SharedKey"]
                     ?? Environment.GetEnvironmentVariable("WORKER_SHARED_API_KEY");

    private readonly IHostEnvironment _env = env;

    public const string HeaderName = "X-Worker-Key";

    public bool IsAuthorized(HttpContext httpContext)
    {
        if (httpContext == null)
        {
            return false;
        }

        // Allow bypass when no key configured and environment is Development or Testing.
        if (string.IsNullOrWhiteSpace(_sharedKey))
        {
            return _env.IsDevelopment() || _env.IsEnvironment("Testing");
        }

        if (!httpContext.Request.Headers.TryGetValue(HeaderName, out StringValues values))
        {
            return false;
        }

        string presented = values.ToString();
        if (string.IsNullOrWhiteSpace(presented))
        {
            return false;
        }

        // Constant time compare minimal implementation (length + equality) – adequate for single shared key.
        if (presented.Length != _sharedKey.Length)
        {
            return false;
        }

        bool equal = true;
        for (int i = 0; i < presented.Length; i++)
        {
            equal &= presented[i] == _sharedKey[i];
        }

        return equal;
    }
}
