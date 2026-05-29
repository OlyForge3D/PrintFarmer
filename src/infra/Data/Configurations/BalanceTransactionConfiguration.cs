using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class BalanceTransactionConfiguration : IEntityTypeConfiguration<BalanceTransaction>
{
    public void Configure(EntityTypeBuilder<BalanceTransaction> builder)
    {
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Amount).HasPrecision(18, 4);
        builder.Property(t => t.TransactionType).HasConversion<int>();
        builder.Property(t => t.Description).HasMaxLength(500);
        builder.Property(t => t.PerformedBy).HasMaxLength(200);

        builder.HasOne(t => t.UserBalance)
            .WithMany()
            .HasForeignKey(t => t.UserBalanceId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => t.UserBalanceId);
        builder.HasIndex(t => t.PrintJobId);
        builder.HasIndex(t => t.CreatedAt);
    }
}
