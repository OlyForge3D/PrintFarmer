using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for RefreshToken (JWT refresh token management).
/// </summary>
public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        _ = builder.HasKey(rt => rt.Id);
        _ = builder.Property(rt => rt.Token).IsRequired().HasMaxLength(512);
        _ = builder.Property(rt => rt.CreatedByIp).IsRequired().HasMaxLength(45);
        _ = builder.Property(rt => rt.RevokedByIp).HasMaxLength(45);
        _ = builder.Property(rt => rt.ReplacedByToken).HasMaxLength(512);

        // Foreign Key
        _ = builder.HasOne(rt => rt.User)
            .WithMany()
            .HasForeignKey(rt => rt.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes
        _ = builder.HasIndex(rt => rt.Token).IsUnique();
        _ = builder.HasIndex(rt => rt.UserId);
        _ = builder.HasIndex(rt => rt.ExpiresAt);
        _ = builder.HasIndex(rt => rt.IsRevoked);
    }
}
