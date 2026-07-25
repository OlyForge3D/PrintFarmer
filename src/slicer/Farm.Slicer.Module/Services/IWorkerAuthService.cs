using Microsoft.AspNetCore.Http;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Validates worker API key headers against configured shared key.
/// </summary>
public interface IWorkerAuthService
{
    /// <summary>
    /// Returns true if the request contains a valid worker API key header.
    /// </summary>
    /// <param name="httpContext">The HTTP request context to validate.</param>
    bool IsAuthorized(HttpContext httpContext);
}
