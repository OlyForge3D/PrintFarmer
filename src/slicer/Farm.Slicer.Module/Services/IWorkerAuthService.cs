using Farm.Slicer.Module.Domain;
using Microsoft.AspNetCore.Http;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Resolves workers from registry-issued service credentials.
/// </summary>
public interface IWorkerAuthService
{
    /// <summary>
    /// Authenticates the worker service identity and its registry-issued key.
    /// </summary>
    /// <param name="httpContext">The HTTP request context to validate.</param>
    /// <returns>The enabled worker bound to the presented credential, or <see langword="null"/>.</returns>
    Task<Worker?> AuthenticateAsync(HttpContext httpContext);
}
