using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.AspNetCore.Hosting;

namespace Farm.Web.Api.Services.StorageManagement;

/// <summary>
/// ASP.NET Core implementation of IApplicationPathProvider.
/// Wraps IWebHostEnvironment to provide application path information to Infrastructure services.
/// </summary>
public class AspNetCorePathProvider : IApplicationPathProvider
{
    private readonly IWebHostEnvironment _environment;

    public AspNetCorePathProvider(IWebHostEnvironment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public string GetContentRootPath() => _environment.ContentRootPath;

    public string GetWebRootPath() => _environment.WebRootPath;
}
