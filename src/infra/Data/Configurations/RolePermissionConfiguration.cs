using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for RolePermission (RBAC role-resource-action mapping).
/// </summary>
public class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        _ = builder.HasKey(rp => rp.Id);

        // Foreign Keys
        _ = builder.HasOne(rp => rp.Role)
            .WithMany(r => r.RolePermissions)
            .HasForeignKey(rp => rp.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasOne(rp => rp.Resource)
            .WithMany(r => r.RolePermissions)
            .HasForeignKey(rp => rp.ResourceId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasOne(rp => rp.Action)
            .WithMany(ua => ua.RolePermissions)
            .HasForeignKey(rp => rp.ActionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique constraint - one permission per role-resource-action combination
        _ = builder.HasIndex(rp => new { rp.RoleId, rp.ResourceId, rp.ActionId }).IsUnique();
    }
}
