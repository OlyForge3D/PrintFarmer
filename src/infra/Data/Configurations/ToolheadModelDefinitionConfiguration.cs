using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class ToolheadModelDefinitionConfiguration : IEntityTypeConfiguration<ToolheadModelDefinition>
{
    public void Configure(EntityTypeBuilder<ToolheadModelDefinition> builder)
    {
        _ = builder.HasKey(t => t.Id);
        _ = builder.Property(t => t.Name).IsRequired().HasMaxLength(128);
        _ = builder.Property(t => t.Description).HasMaxLength(512);

        // Foreign Key to Manufacturer (nullable - community designs may not have a manufacturer)
        _ = builder.HasOne(t => t.Manufacturer)
            .WithMany()
            .HasForeignKey(t => t.ManufacturerId)
            .OnDelete(DeleteBehavior.SetNull);

        // Index for lookups
        _ = builder.HasIndex(t => t.ManufacturerId);
        _ = builder.HasIndex(t => t.Name);
    }
}
