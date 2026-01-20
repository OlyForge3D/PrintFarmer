using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class SpoolmanConfig
{
    public int Id { get; set; } // Single row table; use Id = 1

    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Persisted as text for EF/DTO; use BaseUri for typed access")]
    public string BaseUrl { get; set; } = string.Empty;

    [NotMapped]
    public Uri? BaseUri
    {
        get => Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? u) ? u : null;
        set => BaseUrl = value?.ToString() ?? string.Empty;
    }
}
