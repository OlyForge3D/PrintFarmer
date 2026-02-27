using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class PrinterModelConfiguration : IEntityTypeConfiguration<PrinterModel>
{
    public void Configure(EntityTypeBuilder<PrinterModel> builder)
    {
        _ = builder.HasKey(m => m.Id);
        _ = builder.Property(m => m.Name).IsRequired().HasMaxLength(128);

        _ = builder.HasOne(m => m.Manufacturer)
            .WithMany(x => x.Models)
            .HasForeignKey(m => m.ManufacturerId)
            .OnDelete(DeleteBehavior.NoAction); // Changed from Cascade to NoAction to prevent multiple cascade paths

        // Persisted shadow column for cross-provider case-insensitive uniqueness inside a manufacturer.
        _ = builder.Property<string>("NameLowered")
            .HasColumnName("NameLowered")
            .HasMaxLength(128)
            .IsRequired();
        _ = builder.HasIndex(nameof(PrinterModel.ManufacturerId), "NameLowered").IsUnique();

        // Basic properties
        _ = builder.Property(m => m.MotionType); // MotionType enum stored as int
        _ = builder.Property(m => m.MaxX);
        _ = builder.Property(m => m.MaxY);
        _ = builder.Property(m => m.MaxZ);
        _ = builder.Property(m => m.DefaultBackend);

        // Capability defaults (nozzle diameter and max hotend temp are now on toolheads)
        _ = builder.Property(m => m.HasHeatedBed).HasDefaultValue(true);
        _ = builder.Property(m => m.HasEnclosure).HasDefaultValue(false);
        _ = builder.Property(m => m.HasCarbonFilter).HasDefaultValue(false);
        _ = builder.Property(m => m.HasHepaFilter).HasDefaultValue(false);
        _ = builder.Property(m => m.HasBowdenTube).HasDefaultValue(false);
        _ = builder.Property(m => m.HasPtfeLiner).HasDefaultValue(false);
        _ = builder.Property(m => m.HasLinearRails).HasDefaultValue(false);
        _ = builder.Property(m => m.HasLeadScrews).HasDefaultValue(false);
        _ = builder.Property(m => m.HasToolchanger).HasDefaultValue(false);
        _ = builder.Property(m => m.HasFilamentCutter).HasDefaultValue(false);
        _ = builder.Property(m => m.HasHeatedChamber).HasDefaultValue(false);
        _ = builder.Property(m => m.MultiMaterial).HasDefaultValue(false);
        _ = builder.Property(m => m.SupportsAutoLeveling).HasDefaultValue(false);
        _ = builder.Property(m => m.MaxBedTemp).HasDefaultValue(120);
        _ = builder.Property(m => m.MaxPrintSpeed).HasDefaultValue(150);

        // Configure many-to-many relationship between PrinterModel and FilamentType using skip navigation
        _ = builder.HasMany(p => p.SupportedFilamentTypes)
            .WithMany(f => f.PrinterModels);
    }
}
