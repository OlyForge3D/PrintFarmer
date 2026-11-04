using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public sealed class BedClearEvaluationConfiguration : IEntityTypeConfiguration<BedClearEvaluation>
{
    public void Configure(EntityTypeBuilder<BedClearEvaluation> builder)
    {
        builder.ToTable("BedClearEvaluations");
        builder.HasKey(evaluation => evaluation.Id);

        builder.Property(evaluation => evaluation.AcknowledgedBySubject)
            .IsRequired()
            .HasMaxLength(256);
        builder.Property(evaluation => evaluation.ImageFilePath)
            .HasMaxLength(500);
        builder.Property(evaluation => evaluation.ReasonCode)
            .HasMaxLength(128);
        builder.Property(evaluation => evaluation.GroundTruthBySubject)
            .HasMaxLength(256);
        builder.Property(evaluation => evaluation.State)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(evaluation => evaluation.Verdict)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(evaluation => evaluation.GroundTruth)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.HasOne<Printer>()
            .WithMany()
            .HasForeignKey(evaluation => evaluation.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Camera>()
            .WithMany()
            .HasForeignKey(evaluation => evaluation.CameraId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<PrintJob>()
            .WithMany()
            .HasForeignKey(evaluation => evaluation.PrintJobId)
            .OnDelete(DeleteBehavior.SetNull);

        // Keep capture-time plate provenance even when the catalog entry is removed.
        builder.HasIndex(evaluation => evaluation.BedTypeId);

        builder.HasIndex(evaluation => new
        {
            evaluation.PrinterId,
            evaluation.CameraId,
            evaluation.BedTypeId,
            evaluation.IsActiveReference,
            evaluation.GroundTruth,
        }).HasDatabaseName("IX_BedClearEvaluations_ReferenceScope");

        builder.HasIndex(evaluation => evaluation.State)
            .HasDatabaseName("IX_BedClearEvaluations_State");

        builder.HasIndex(evaluation => evaluation.SourceAcknowledgementId)
            .IsUnique()
            .HasDatabaseName("UX_BedClearEvaluations_Acknowledgement");
    }
}
