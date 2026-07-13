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
        _ = builder.HasIndex(g => new { g.PrinterId, g.Name })
            .IsUnique()
            .HasDatabaseName("UX_FilamentFallbackGroups_PrinterId_Name");
    }
}

public class FilamentFallbackGroupMemberConfiguration : IEntityTypeConfiguration<FilamentFallbackGroupMember>
{
    public void Configure(EntityTypeBuilder<FilamentFallbackGroupMember> builder)
    {
        _ = builder.HasKey(m => m.Id);

        _ = builder.HasOne(m => m.Toolhead)
            .WithMany()
            .HasForeignKey(m => m.ToolheadId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasIndex(m => m.FallbackGroupId);
        _ = builder.HasIndex(m => new { m.FallbackGroupId, m.Position })
            .IsUnique()
            .HasDatabaseName("UX_FilamentFallbackGroupMembers_GroupId_Position");
        _ = builder.HasIndex(m => new { m.FallbackGroupId, m.ToolheadId })
            .IsUnique()
            .HasDatabaseName("UX_FilamentFallbackGroupMembers_GroupId_ToolheadId");
    }
}
