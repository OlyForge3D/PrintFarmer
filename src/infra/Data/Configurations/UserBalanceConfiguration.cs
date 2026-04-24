using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class UserBalanceConfiguration : IEntityTypeConfiguration<UserBalance>
{
    public void Configure(EntityTypeBuilder<UserBalance> builder)
    {
        builder.HasKey(b => b.Id);

        builder.Property(b => b.BalanceAmount).HasPrecision(18, 4);
        builder.Property(b => b.Currency).IsRequired().HasMaxLength(3).HasDefaultValue("USD");

        builder.HasOne(b => b.User)
            .WithMany()
            .HasForeignKey(b => b.UserId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // One balance per user
        builder.HasIndex(b => b.UserId).IsUnique();
    }
}
