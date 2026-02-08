using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for UserRole (user-role assignment).
/// </summary>
public class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        _ = builder.HasKey(ur => ur.Id);

        // Foreign Keys
        _ = builder.HasOne(ur => ur.User)
            .WithMany(u => u.UserRoles)
            .HasForeignKey(ur => ur.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasOne(ur => ur.Role)
            .WithMany(r => r.UserRoles)
            .HasForeignKey(ur => ur.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique constraint - one assignment per user-role combination
        _ = builder.HasIndex(ur => new { ur.UserId, ur.RoleId }).IsUnique();
        _ = builder.HasIndex(ur => ur.IsActive);
        _ = builder.HasIndex(ur => ur.ExpiresAt);
    }
}
