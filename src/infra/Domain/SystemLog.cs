using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class SystemLog
{
    public int Id { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string Level { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Exception { get; set; }

    public string? Source { get; set; }

    public string? CorrelationId { get; set; } // For end-to-end tracing

    public string? Metadata { get; set; } // JSON metadata for arbitrary context
}
