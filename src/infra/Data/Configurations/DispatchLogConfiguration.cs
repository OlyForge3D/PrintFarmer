using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class DispatchLogConfiguration : IEntityTypeConfiguration<DispatchLog>
{
    public void Configure(EntityTypeBuilder<DispatchLog> builder)
    {
        _ = builder.HasKey(d => d.Id);

        _ = builder.Property(d => d.Action)
            .HasConversion<string>()
            .HasMaxLength(20);

        _ = builder.Property(d => d.DispatchMode)
            .HasConversion<string>()
            .HasMaxLength(20);

        _ = builder.Property(d => d.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        _ = builder.Property(d => d.Reason).HasMaxLength(500);
        _ = builder.Property(d => d.ScoreBreakdown).HasMaxLength(4000);
        _ = builder.Property(d => d.ScoringDetails).HasMaxLength(8000);
        _ = builder.Property(d => d.ErrorMessage).HasMaxLength(2000);
        _ = builder.Property(d => d.DispatchedByUserId).HasMaxLength(450);

        _ = builder.HasOne(d => d.PrintJob)
            .WithMany()
            .HasForeignKey(d => d.PrintJobId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasOne(d => d.Printer)
            .WithMany()
            .HasForeignKey(d => d.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasIndex(d => d.PrintJobId);
        _ = builder.HasIndex(d => d.PrinterId);
        _ = builder.HasIndex(d => d.CreatedAtUtc);
        _ = builder.HasIndex(d => d.DispatchedAt);
    }
}
