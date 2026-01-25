using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for ProcessProfile - slicer process/print settings profiles.
/// </summary>
public class ProcessProfileConfiguration : IEntityTypeConfiguration<ProcessProfile>
{
    public void Configure(EntityTypeBuilder<ProcessProfile> builder)
    {
        _ = builder.HasKey(p => p.Id);

        // Properties
        _ = builder.Property(p => p.Name).IsRequired().HasMaxLength(255);
        _ = builder.Property(p => p.Description).HasMaxLength(1000);
        _ = builder.Property(p => p.SlicerType).HasConversion<int>();
        _ = builder.Property(p => p.Quality).HasConversion<int>();
        _ = builder.Property(p => p.AdvancedSettings).HasColumnType("TEXT");
        _ = builder.Property(p => p.RawJson).HasColumnType("TEXT");
        _ = builder.Property(p => p.SettingsJson).HasColumnType("TEXT");
        _ = builder.Property(p => p.Hash).HasMaxLength(64);
        _ = builder.Property(p => p.IsSystem).HasDefaultValue(false);

        // Foreign Keys
        _ = builder.HasOne(p => p.PrinterModel)
            .WithMany()
            .HasForeignKey(p => p.PrinterModelId)
            .OnDelete(DeleteBehavior.SetNull);

        _ = builder.HasOne(p => p.SpecificPrinter)
            .WithMany()
            .HasForeignKey(p => p.SpecificPrinterId)
            .OnDelete(DeleteBehavior.SetNull);

        _ = builder.HasOne(p => p.CreatedByUser)
            .WithMany()
            .HasForeignKey(p => p.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Indexes
        _ = builder.HasIndex(p => new { p.Name, p.SlicerType, p.PrinterModelId }).IsUnique();
        _ = builder.HasIndex(p => p.SlicerType);
        _ = builder.HasIndex(p => p.PrinterModelId);
        _ = builder.HasIndex(p => p.IsDefault);
        _ = builder.HasIndex(p => p.IsPublic);
        _ = builder.HasIndex(p => p.CreatedByUserId);
        _ = builder.HasIndex(p => p.Hash).IsUnique();
        _ = builder.HasIndex(p => p.IsSystem);
    }
}
