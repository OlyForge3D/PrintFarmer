using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class MaterialClusterMemberConfiguration : IEntityTypeConfiguration<MaterialClusterMember>
{
    public void Configure(EntityTypeBuilder<MaterialClusterMember> builder)
    {
        _ = builder.HasKey(m => new { m.ClusterId, m.FilamentTypeId });

        _ = builder.HasOne(m => m.Cluster)
            .WithMany(c => c.Members)
            .HasForeignKey(m => m.ClusterId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasOne(m => m.FilamentType)
            .WithMany()
            .HasForeignKey(m => m.FilamentTypeId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.Property(m => m.AddedAt).IsRequired();
    }
}
