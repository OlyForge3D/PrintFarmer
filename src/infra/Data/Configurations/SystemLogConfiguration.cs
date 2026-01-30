using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for SystemLog (application logging).
/// </summary>
public class SystemLogConfiguration : IEntityTypeConfiguration<SystemLog>
{
    public void Configure(EntityTypeBuilder<SystemLog> builder)
    {
        _ = builder.HasKey(l => l.Id);
        _ = builder.Property(l => l.CorrelationId).HasMaxLength(64);
        _ = builder.Property(l => l.Exception).HasColumnType("TEXT");
        _ = builder.Property(l => l.Level).IsRequired().HasMaxLength(32);
        _ = builder.Property(l => l.Message).IsRequired().HasMaxLength(1024);
        _ = builder.Property(l => l.Metadata).HasColumnType("TEXT");
        _ = builder.Property(l => l.Source).HasMaxLength(128);
        _ = builder.Property(l => l.Timestamp).IsRequired();
        _ = builder.HasIndex(l => l.Timestamp);
        _ = builder.HasIndex(l => l.Level);
    }
}
