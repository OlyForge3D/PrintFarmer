using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class FilamentFallbackGroupConfiguration : IEntityTypeConfiguration<FilamentFallbackGroup>
{
    public void Configure(EntityTypeBuilder<FilamentFallbackGroup> builder)
    {
        _ = builder.HasKey(g => g.Id);

        _ = builder.Property(g => g.Name).IsRequired().HasMaxLength(128);
        _ = builder.Property(g => g.NameNormalized).IsRequired().HasMaxLength(128);
        _ = builder.Property(g => g.MaterialType).IsRequired().HasMaxLength(64);

        _ = builder.HasOne(g => g.Printer)
            .WithMany()
            .HasForeignKey(g => g.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasMany(g => g.Members)
            .WithOne(m => m.FallbackGroup!)
            .HasForeignKey(m => m.FallbackGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasIndex(g => g.PrinterId);

        // Unique over the case-folded name so per-printer name uniqueness is enforced
        // case-insensitively at the database level (matches the service-layer ToLower check).
        // See FilamentFallbackGroup.NameNormalized. Issue #711 (F6 remediation, FIX A).
        _ = builder.HasIndex(g => new { g.PrinterId, g.NameNormalized })
            .IsUnique()
            .HasDatabaseName("UX_FilamentFallbackGroups_PrinterId_NameNormalized");
    }
}

public class FilamentFallbackGroupMemberConfiguration : IEntityTypeConfiguration<FilamentFallbackGroupMember>
{
    public void Configure(EntityTypeBuilder<FilamentFallbackGroupMember> builder)
    {
        _ = builder.HasKey(m => m.Id);

        // Toolhead FK uses Restrict (not Cascade) to avoid multiple cascade paths on SQL
        // Server (error 1785). Both FilamentFallbackGroup and Toolhead cascade from Printer,
        // and a member cascades from its owning group — a second Cascade to Toolhead would
        // form a diamond that SQL Server rejects. Member rows are cleaned up via the owning
        // group's Cascade (which fires when the printer is deleted, since the group cascades
        // from the printer). Direct Toolhead deletion is not a supported operation — printers
        // cascade to their toolheads and to their fallback groups directly. Issue #711 (F6).
        _ = builder.HasOne(m => m.Toolhead)
            .WithMany()
            .HasForeignKey(m => m.ToolheadId)
            .OnDelete(DeleteBehavior.Restrict);

        _ = builder.HasIndex(m => m.FallbackGroupId);
        _ = builder.HasIndex(m => new { m.FallbackGroupId, m.Position })
            .IsUnique()
            .HasDatabaseName("UX_FilamentFallbackGroupMembers_GroupId_Position");
        _ = builder.HasIndex(m => new { m.FallbackGroupId, m.ToolheadId })
            .IsUnique()
            .HasDatabaseName("UX_FilamentFallbackGroupMembers_GroupId_ToolheadId");
    }
}
