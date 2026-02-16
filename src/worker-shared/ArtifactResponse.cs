using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Slicer.Worker.Core;

/// <summary>
/// Response from artifact upload endpoint
/// </summary>
public class ArtifactResponse
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    public string Sha256Hash { get; set; } = string.Empty;

    public string FileUrl { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
