using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class HarvestDiscoveredFileConfiguration : IEntityTypeConfiguration<HarvestDiscoveredFile>
{
    public void Configure(EntityTypeBuilder<HarvestDiscoveredFile> builder)
    {
        _ = builder.HasKey(f => f.Id);
        _ = builder.Property(f => f.HarvestOperationId).IsRequired();
        _ = builder.Property(f => f.FilePath).IsRequired().HasMaxLength(512);
        _ = builder.Property(f => f.FileName).IsRequired().HasMaxLength(256);
        _ = builder.Property(f => f.Size).IsRequired();
        _ = builder.Property(f => f.ThumbnailUrl).HasMaxLength(512);
        _ = builder.Property(f => f.Status).IsRequired();
        _ = builder.Property(f => f.Error).HasMaxLength(512);
        _ = builder.Property(f => f.DiscoveredAt).IsRequired();
        _ = builder.Property(f => f.StartedAt);
        _ = builder.Property(f => f.CompletedAt);
        _ = builder.HasIndex(f => f.HarvestOperationId);
    }
}
