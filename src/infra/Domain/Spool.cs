using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class Spool
{
    public Guid Id { get; set; }

    /// <summary>Concurrency token for optimistic locking during concurrent printer assignment.</summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public string Material { get; set; } = string.Empty;

    public double WeightGrams { get; set; }

    public string ColorHex { get; set; } = "#000000";

    public bool InUse { get; set; }

    public Guid? AssignedPrinterId { get; set; }
}
