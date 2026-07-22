namespace Farm.Infrastructure.Domain;

/// <summary>
/// Join entity linking a <see cref="FilamentType"/> to a <see cref="MaterialCluster"/>.
/// Uses a composite key (ClusterId, FilamentTypeId).
/// </summary>
public class MaterialClusterMember
{
    public Guid ClusterId { get; set; }

    public MaterialCluster Cluster { get; set; } = null!;

    public Guid FilamentTypeId { get; set; }

    public FilamentType FilamentType { get; set; } = null!;

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
