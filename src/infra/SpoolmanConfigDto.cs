using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// Spoolman integration
/// <summary>
/// Configuration settings for integrating with an external Spoolman instance.
/// </summary>
// Made BaseUrl nullable so that an empty JSON object posted to probe endpoint doesn't trigger automatic 400 from [ApiController].
public record SpoolmanConfigDto(string? BaseUrl)
{
    [JsonIgnore] public Uri? BaseUri => string.IsNullOrWhiteSpace(BaseUrl) ? null : (Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? u) ? u : null);
}
