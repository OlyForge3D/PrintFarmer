using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for PasswordResetToken (password reset flow).
/// </summary>
public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        _ = builder.HasKey(prt => prt.Id);
        _ = builder.Property(prt => prt.Token).IsRequired().HasMaxLength(256);
        _ = builder.Property(prt => prt.UsedByIp).HasMaxLength(45);

        // Foreign Key
        _ = builder.HasOne(prt => prt.User)
            .WithMany()
            .HasForeignKey(prt => prt.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes
        _ = builder.HasIndex(prt => prt.Token).IsUnique();
        _ = builder.HasIndex(prt => prt.UserId);
        _ = builder.HasIndex(prt => prt.ExpiresAt);
        _ = builder.HasIndex(prt => prt.IsUsed);
    }
}
