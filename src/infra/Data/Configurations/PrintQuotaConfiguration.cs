using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class PrintQuotaConfiguration : IEntityTypeConfiguration<PrintQuota>
{
    public void Configure(EntityTypeBuilder<PrintQuota> builder)
    {
        builder.HasKey(q => q.Id);

        builder.Property(q => q.GroupName).HasMaxLength(200);
        builder.Property(q => q.QuotaType).HasConversion<int>();
        builder.Property(q => q.LimitAmount).HasPrecision(18, 4);
        builder.Property(q => q.UsedAmount).HasPrecision(18, 4);
        builder.Property(q => q.PeriodType).HasConversion<int>();
        builder.Property(q => q.IsActive).HasDefaultValue(true);
        builder.Property(q => q.Notes).HasMaxLength(500);

        builder.HasOne(q => q.User)
            .WithMany()
            .HasForeignKey(q => q.UserId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(q => q.UserId);
        builder.HasIndex(q => q.GroupName);
        builder.HasIndex(q => q.IsActive);
        builder.HasIndex(q => q.ResetAt)
            .HasDatabaseName("IX_PrintQuotas_ResetAt");
    }
}
