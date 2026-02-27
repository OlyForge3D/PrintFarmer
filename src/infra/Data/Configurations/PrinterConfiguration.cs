using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for the Printer entity.
/// Extracted from AppDbContext.OnModelCreating for better maintainability.
/// </summary>
public class PrinterConfiguration : IEntityTypeConfiguration<Printer>
{
    public void Configure(EntityTypeBuilder<Printer> builder)
    {
        builder.HasKey(p => p.Id);

        // Concurrency token for optimistic locking
        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.Property(p => p.Name).IsRequired().HasMaxLength(128);
        builder.Property(p => p.ServerUrl).IsRequired().HasMaxLength(256);
        builder.Property(p => p.OriginalServerUrl).HasMaxLength(256);
        builder.Property(p => p.Backend).HasDefaultValue(0);
        builder.Property(p => p.ApiKey);
        builder.Property(p => p.DateAcquired);

        // Prevent duplicate printers by ServerUrl (unique constraint)
        builder.HasIndex(p => p.ServerUrl).IsUnique();

        // Foreign key relationships
        builder.HasOne(p => p.Manufacturer)
            .WithMany()
            .HasForeignKey(p => p.ManufacturerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.Model)
            .WithMany()
            .HasForeignKey(p => p.ModelId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.Location)
            .WithMany(l => l.Printers)
            .HasForeignKey(p => p.LocationId)
            .OnDelete(DeleteBehavior.SetNull);

        // Toolheads collection - one printer can have multiple hotends
        builder.HasMany(p => p.Toolheads)
            .WithOne(t => t.Printer)
            .HasForeignKey(t => t.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        // Tags - many-to-many via skip-navigation (auto-creates join table)
        builder.HasMany(p => p.Tags).WithMany();
    }
}
