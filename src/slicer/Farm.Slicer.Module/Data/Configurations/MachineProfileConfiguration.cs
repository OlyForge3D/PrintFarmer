using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Slicer.Module.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="MachineProfile"/> — slicer machine/printer configuration profiles.
/// </summary>
/// <remarks>
/// Cross-domain references (PrinterModel, User) are stored as nullable <see cref="Guid"/>
/// columns with indexes but no FK constraints.
/// The <see cref="MachineProfile.MachineModelProfile"/> navigation is a slicer-internal relationship.
/// </remarks>
public class MachineProfileConfiguration : IEntityTypeConfiguration<MachineProfile>
{
    /// <summary>
    /// Name of the unique <c>(Name, SlicerType)</c> index over unowned (system/stock) rows. Its
    /// <c>CreatedByUserId IS NULL</c> filter is provider-specific SQL, so it is applied in
    /// <see cref="SlicerDbContext"/> rather than here.
    /// </summary>
    public const string UnownedNameUniqueIndexName = "IX_MachineProfiles_Name_SlicerType_Unowned";

    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<MachineProfile> builder)
    {
        _ = builder.HasKey(p => p.Id);

        // Properties
        _ = builder.Property(p => p.Name).IsRequired().HasMaxLength(255);
        _ = builder.Property(p => p.Manufacturer).IsRequired().HasMaxLength(255);
        _ = builder.Property(p => p.Description).HasMaxLength(1000);
        _ = builder.Property(p => p.SlicerType).HasConversion<int>();
        _ = builder.Property(p => p.RawJson).HasColumnType("TEXT");
        _ = builder.Property(p => p.SettingsJson).HasColumnType("TEXT");
        _ = builder.Property(p => p.Hash).HasMaxLength(64);
        _ = builder.Property(p => p.IsSystem).HasDefaultValue(false);
        _ = builder.Property(p => p.SourceSystemPresetName).HasMaxLength(255);
        _ = builder.Property(p => p.OverridesJson).HasColumnType("TEXT");

        // Slicer-internal FK: MachineProfile → MachineModelProfile
        _ = builder.HasOne(p => p.MachineModelProfile)
            .WithMany(m => m.MachineProfiles)
            .HasForeignKey(p => p.MachineModelProfileId)
            .OnDelete(DeleteBehavior.SetNull);

        // Soft-reference indexes (no FK constraints)
        _ = builder.HasIndex(p => p.PrinterModelId);
        _ = builder.HasIndex(p => p.CreatedByUserId);

        // Indexes
        // Name uniqueness is scoped per owner (#3198, mirroring #3192 for filaments): a global index
        // made one user's private profile name block another user's upload/clone/rename with a 500,
        // which also disclosed that the name existed. EF Core auto-generates the "IS NOT NULL"
        // filter for the nullable owner column on SQL Server; PostgreSQL and SQLite treat NULL
        // owners as distinct. Unowned (system/stock) rows keep global name uniqueness through
        // UnownedNameUniqueIndexName, the backstop for the system import paths (#1779).
        _ = builder.HasIndex(p => new { p.CreatedByUserId, p.Name, p.SlicerType }).IsUnique();
        _ = builder.HasIndex(p => new { p.Name, p.SlicerType })
            .IsUnique()
            .HasDatabaseName(UnownedNameUniqueIndexName);
        _ = builder.HasIndex(p => p.SlicerType);
        _ = builder.HasIndex(p => p.Manufacturer);
        _ = builder.HasIndex(p => p.Hash).IsUnique();
        _ = builder.HasIndex(p => p.IsSystem);
    }
}
