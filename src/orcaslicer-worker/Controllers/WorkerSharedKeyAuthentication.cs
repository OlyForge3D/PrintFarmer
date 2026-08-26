using System.Security.Cryptography;
using System.Text;
using Farm.Slicer.Module.Services.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Farm.OrcaSlicer.Worker.Controllers;

/// <summary>
/// Validates the existing slicer bootstrap key for worker management routes.
/// </summary>
public sealed class WorkerSharedKeyValidator
{
    private readonly byte[] _sharedKey;

    /// <summary>
    /// Creates a validator from the canonical WorkerAuth:SharedKey setting.
    /// </summary>
    /// <param name="configuration">Worker configuration.</param>
    public WorkerSharedKeyValidator(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? key = WorkerAuthConfiguration.ResolveSharedKey(configuration)?.Value;
        _sharedKey = string.IsNullOrWhiteSpace(key)
            ? []
            : Encoding.UTF8.GetBytes(key);
    }

    /// <summary>
    /// Validates a presented key without data-dependent string comparison.
    /// </summary>
    /// <param name="presentedKey">Key presented in the request header.</param>
    /// <returns><see langword="true"/> when the configured and presented keys match.</returns>
    public bool Validate(string? presentedKey)
    {
        if (_sharedKey.Length == 0 || string.IsNullOrWhiteSpace(presentedKey))
        {
            return false;
        }

        byte[] presentedBytes = Encoding.UTF8.GetBytes(presentedKey);
        return presentedBytes.Length == _sharedKey.Length
            && CryptographicOperations.FixedTimeEquals(
                presentedBytes,
                _sharedKey);
    }
}

/// <summary>
/// Requires the existing shared slicer bootstrap credential.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireWorkerSharedKeyAttribute : Attribute, IAsyncActionFilter
{
    /// <summary>Canonical shared slicer key header.</summary>
    public const string HeaderName = "X-Slicer-Api-Key";

    /// <summary>Legacy compact header accepted by existing slicer clients.</summary>
    public const string AlternateHeaderName = "X-Slicer-ApiKey";

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        WorkerSharedKeyValidator? validator =
            context.HttpContext.RequestServices
                .GetService<WorkerSharedKeyValidator>();
        string? presentedKey = context.HttpContext.Request.Headers[HeaderName]
            .FirstOrDefault()
            ?? context.HttpContext.Request.Headers[AlternateHeaderName]
                .FirstOrDefault();
        if (validator is null || !validator.Validate(presentedKey))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Authentication required",
                Detail = "A valid slicer worker management key is required.",
                Extensions =
                {
                    ["code"] = "authentication_required",
                },
            })
            {
                StatusCode = StatusCodes.Status401Unauthorized,
                ContentTypes = { "application/problem+json" },
            };
            return;
        }

        await next();
    }
}
