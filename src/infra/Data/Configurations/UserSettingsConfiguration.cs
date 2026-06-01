using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class UserSettingsConfiguration : IEntityTypeConfiguration<UserSettings>
{
    public void Configure(EntityTypeBuilder<UserSettings> builder)
    {
        _ = builder.HasKey(u => u.Id);
        _ = builder.HasIndex(u => u.UserId).IsUnique();
        _ = builder.Property(u => u.Theme).IsRequired().HasMaxLength(32);
        _ = builder.Property(u => u.Locale).IsRequired().HasMaxLength(16);
        _ = builder.Property(u => u.ItemsPerPage).IsRequired();
        _ = builder.Property(u => u.DefaultSlicerPreset).HasMaxLength(256);
        _ = builder.Property(u => u.UpdatedAt).IsRequired();
        _ = builder.Property(u => u.RowVersion).IsConcurrencyToken();

        _ = builder.HasOne(u => u.User)
            .WithMany()
            .HasForeignKey(u => u.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
