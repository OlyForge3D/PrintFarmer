using System.Text.Json;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for PrinterModelToolhead (template toolheads for printer models).
/// </summary>
public class PrinterModelToolheadConfiguration : IEntityTypeConfiguration<PrinterModelToolhead>
{
    public void Configure(EntityTypeBuilder<PrinterModelToolhead> builder)
    {
        _ = builder.HasKey(t => t.Id);
        _ = builder.Property(t => t.Name).HasMaxLength(128);
        _ = builder.Property(t => t.Index).IsRequired();
        _ = builder.Property(t => t.IsPrimary).HasDefaultValue(false);

        // JSON array properties
        _ = builder.Property(t => t.SupportedMaterials)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<string[]>(v, (JsonSerializerOptions?)null));
        builder.Property(t => t.SupportedMaterials).Metadata.SetValueComparer(
            new ValueComparer<string[]?>(
                (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
                c => c == null ? 0 : c.Aggregate(0, (h, v) => HashCode.Combine(h, v.GetHashCode())),
                c => c == null ? null : c.ToArray()));

        // Foreign Key to PrinterModel
        _ = builder.HasOne(t => t.PrinterModel)
            .WithMany(p => p.Toolheads)
            .HasForeignKey(t => t.PrinterModelId)
            .OnDelete(DeleteBehavior.Cascade);

        // Foreign Keys to Component Models (optional relationships)
        _ = builder.HasOne(t => t.HotendModel)
            .WithMany()
            .HasForeignKey(t => t.HotendModelId)
            .OnDelete(DeleteBehavior.SetNull);

        _ = builder.HasOne(t => t.ExtruderModel)
            .WithMany()
            .HasForeignKey(t => t.ExtruderModelId)
            .OnDelete(DeleteBehavior.SetNull);

        _ = builder.HasOne(t => t.ToolheadModelDef)
            .WithMany()
            .HasForeignKey(t => t.ToolheadModelDefId)
            .OnDelete(DeleteBehavior.SetNull);

        _ = builder.HasOne(t => t.NozzleModel)
            .WithMany()
            .HasForeignKey(t => t.NozzleModelId)
            .OnDelete(DeleteBehavior.SetNull);

        // Indexes
        _ = builder.HasIndex(t => t.PrinterModelId);
        _ = builder.HasIndex(t => t.Index);
    }
}
