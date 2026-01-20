using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.IO;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// Configuration options for local file storage
/// </summary>
public class LocalFileStorageOptions
{
    public string BasePath { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "storage");

    public string? BaseUrl { get; set; }
}
