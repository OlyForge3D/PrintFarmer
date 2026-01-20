using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class SlicerSettings
{
    public int Id { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public string? PerEngineJson { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public double JitterPercent { get; set; } = 15.0;
}
