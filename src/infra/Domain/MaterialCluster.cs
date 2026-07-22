namespace Farm.Infrastructure.Domain;

/// <summary>
/// Groups equivalent filament types from different vendors so auto-dispatch
/// can pick the right spool when an exact material match is unavailable.
/// Example: a "PLA+" cluster containing Brand A PLA+, Brand B PLA Pro, Brand C PLA Plus.
/// </summary>
public class MaterialCluster
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<MaterialClusterMember> Members { get; set; } = new List<MaterialClusterMember>();
}
