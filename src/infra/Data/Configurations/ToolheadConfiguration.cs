using System.Text.Json;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for the Toolhead entity.
/// Extracted from AppDbContext.OnModelCreating for better maintainability.
/// </summary>
public class ToolheadConfiguration : IEntityTypeConfiguration<Toolhead>
{
    public void Configure(EntityTypeBuilder<Toolhead> builder)
    {
        builder.HasKey(t => t.Id);

        // Basic properties
        builder.Property(t => t.Name).HasMaxLength(128);
        builder.Property(t => t.Index).IsRequired();
        builder.Property(t => t.IsPrimary).HasDefaultValue(false);
        builder.Property(t => t.UpdatedAt).IsRequired();

        // Multi-toolhead filament tracking properties
        builder.Property(t => t.ToolheadType)
            .IsRequired()
            .HasDefaultValue(ToolheadType.Physical);
        builder.Property(t => t.CurrentMaterial).HasMaxLength(64);
        builder.Property(t => t.CurrentFilamentColor).HasMaxLength(32);

        // JSON array properties
        builder.Property(t => t.SupportedMaterials)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<string[]>(v, (JsonSerializerOptions?)null));
        builder.Property(t => t.SupportedMaterials).Metadata.SetValueComparer(
            new ValueComparer<string[]?>(
                (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
                c => c == null ? 0 : c.Aggregate(0, (h, v) => HashCode.Combine(h, v.GetHashCode())),
                c => c == null ? null : c.ToArray()));

        // Foreign Key to Printer
        builder.HasOne(t => t.Printer)
            .WithMany(p => p.Toolheads)
            .HasForeignKey(t => t.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        // Foreign Keys to Component Models (optional relationships)
        builder.HasOne(t => t.HotendModel)
            .WithMany()
            .HasForeignKey(t => t.HotendModelId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(t => t.ExtruderModel)
            .WithMany()
            .HasForeignKey(t => t.ExtruderModelId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(t => t.ToolheadModelDef)
            .WithMany()
            .HasForeignKey(t => t.ToolheadModelDefId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(t => t.NozzleModel)
            .WithMany()
            .HasForeignKey(t => t.NozzleModelId)
            .OnDelete(DeleteBehavior.SetNull);

        // Indexes
        builder.HasIndex(t => t.PrinterId);
        builder.HasIndex(t => t.Index);
        builder.HasIndex(t => t.CurrentSpoolId);
    }
}
