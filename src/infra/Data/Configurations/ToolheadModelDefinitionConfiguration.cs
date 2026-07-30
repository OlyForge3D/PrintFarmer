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

        // Foreign Key to Manufacturer. Required, matching HardwareModel.ManufacturerId
        // being non-nullable. Community designs use the seeded "Community"/"Unknown"
        // manufacturer entries rather than a NULL FK. Restrict prevents removing a
        // manufacturer that still has toolhead models pointing at it and matches the
        // sibling Hotend/Extruder/Nozzle configurations. Prior SetNull was incompatible
        // with the required column on SQL Server (fails CREATE TABLE with error 1761).
        _ = builder.HasOne(t => t.Manufacturer)
            .WithMany()
            .HasForeignKey(t => t.ManufacturerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Index for lookups
        _ = builder.HasIndex(t => t.ManufacturerId);
        _ = builder.HasIndex(t => t.Name);
    }
}
