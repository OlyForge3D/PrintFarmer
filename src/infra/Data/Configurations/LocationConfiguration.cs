using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for the Location entity.
/// Supports self-referential hierarchy (tree structure).
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

        // Tree structure properties
        builder.Property(l => l.Path).IsRequired().HasMaxLength(2048).HasDefaultValue("/");
        builder.Property(l => l.Depth).HasDefaultValue(0);
        builder.Property(l => l.SortOrder).HasDefaultValue(0);
        builder.Property(l => l.TotalPrinterCount).HasDefaultValue(0);

        // Self-referential FK: Parent → Children
        builder.HasOne(l => l.Parent)
            .WithMany(l => l.Children)
            .HasForeignKey(l => l.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        // One location can have many printers
        builder.HasMany(l => l.Printers)
            .WithOne(p => p.Location)
            .HasForeignKey(p => p.LocationId)
            .OnDelete(DeleteBehavior.SetNull);

        // Indexes — composite unique on (ParentId, Name) allows duplicate names under different parents
        builder.HasIndex(l => new { l.ParentId, l.Name }).IsUnique();
        builder.HasIndex(l => l.ParentId);
        builder.HasIndex(l => l.Path);
        builder.HasIndex(l => l.IsActive);
        builder.HasIndex(l => l.CreatedAt);
    }
}
