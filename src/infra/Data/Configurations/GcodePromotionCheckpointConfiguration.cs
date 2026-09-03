using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Configures the durable promotion checkpoint that coordinates the slicer artifact context and the
/// core G-code library without a cross-context transaction.
/// </summary>
public sealed class GcodePromotionCheckpointConfiguration : IEntityTypeConfiguration<GcodePromotionCheckpoint>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<GcodePromotionCheckpoint> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.HasKey(checkpoint => checkpoint.Id);
        _ = builder.Property(checkpoint => checkpoint.OperationScope).IsRequired().HasMaxLength(128);
        _ = builder.Property(checkpoint => checkpoint.OperationId).IsRequired().HasMaxLength(128);
        _ = builder.Property(checkpoint => checkpoint.RequestSha256).IsRequired().HasMaxLength(64);
        _ = builder.Property(checkpoint => checkpoint.VirtualDirectory).IsRequired().HasMaxLength(512);
        _ = builder.Property(checkpoint => checkpoint.SourceContentSha256).IsRequired().HasMaxLength(64);
        _ = builder.Property(checkpoint => checkpoint.FailureCode).HasMaxLength(128);
        _ = builder.Property(checkpoint => checkpoint.State).HasConversion<int>();
        _ = builder.Property(checkpoint => checkpoint.Revision).IsConcurrencyToken().ValueGeneratedNever();

        // One promotion per operation key, and one promotion per source artifact content. Both are
        // enforced by the database so a concurrent replay can only ever produce one G-code file.
        _ = builder.HasIndex(checkpoint => new { checkpoint.OperationScope, checkpoint.OperationId })
            .IsUnique();
        _ = builder.HasIndex(checkpoint => new { checkpoint.SourceArtifactId, checkpoint.SourceContentSha256 })
            .IsUnique();
        _ = builder.HasIndex(checkpoint => checkpoint.GcodeFileId).IsUnique();
        _ = builder.HasIndex(checkpoint => new { checkpoint.State, checkpoint.UpdatedAtUtc });
    }
}
