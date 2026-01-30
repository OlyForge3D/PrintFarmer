using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for the Location entity.
/// Extracted from AppDbContext.OnModelCreating for better maintainability.
/// </summary>
public class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        builder.HasKey(l => l.Id);

        // Basic properties
        builder.Property(l => l.Name).IsRequired().HasMaxLength(256);
        builder.Property(l => l.Description).HasMaxLength(1024);
        builder.Property(l => l.PrinterCount).HasDefaultValue(0);
        builder.Property(l => l.CreatedAt).IsRequired();
        builder.Property(l => l.ModifiedAt).IsRequired();
        builder.Property(l => l.IsActive).HasDefaultValue(true);

        // One location can have many printers
        builder.HasMany(l => l.Printers)
            .WithOne(p => p.Location)
            .HasForeignKey(p => p.LocationId)
            .OnDelete(DeleteBehavior.SetNull);

        // Indexes
        builder.HasIndex(l => l.Name).IsUnique();
        builder.HasIndex(l => l.IsActive);
        builder.HasIndex(l => l.CreatedAt);
    }
}
