using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// EF configuration for <see cref="UserTask"/>. Introduced by issue #713 to add
/// shift-plan compiler fields (anchor, window, canonical source) and their
/// supporting indexes without altering the existing legacy task columns.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="UserTask.AnchorKind"/> and <see cref="UserTask.SourceKind"/>
/// enums are persisted as canonical lowercase strings so unknown/future values
/// added by newer code round-trip through the API layer without breaking
/// clients (per Dallas F8 API contract).
/// </para>
/// <para>
/// Indexes:
/// <list type="bullet">
///   <item><description><c>IX_UserTasks_SourceKind_SourceId</c> — dedupe lookup for the compiler (filtered to non-null source ids where the provider supports it).</description></item>
///   <item><description><c>IX_UserTasks_Status_AnchorKind_AnchorAtUtc</c> — supports the shift-plan grouped/ordered query.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class UserTaskConfiguration : IEntityTypeConfiguration<UserTask>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserTask> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.HasKey(t => t.Id);

        // Store the shift-plan enums as canonical camelCase strings so unknown values are recoverable.
        _ = builder.Property(t => t.AnchorKind)
            .HasConversion(
                v => AnchorKindToWire(v),
                v => AnchorKindFromWire(v))
            .HasMaxLength(32)
            .IsRequired();

        _ = builder.Property(t => t.SourceKind)
            .HasConversion(
                v => SourceKindToWire(v),
                v => SourceKindFromWire(v))
            .HasMaxLength(32)
            .IsRequired();

        _ = builder.Property(t => t.SourceId).HasMaxLength(128);

        // Dedupe index for the shift-plan compiler.
        _ = builder.HasIndex(t => new { t.SourceKind, t.SourceId })
            .HasDatabaseName("IX_UserTasks_SourceKind_SourceId");

        // Shift-plan grouped/ordered query support.
        _ = builder.HasIndex(t => new { t.Status, t.AnchorKind, t.AnchorAtUtc })
            .HasDatabaseName("IX_UserTasks_Status_AnchorKind_AnchorAtUtc");
    }

    // Canonical wire values. Names are stable — do not rename.
    internal static string AnchorKindToWire(UserTaskAnchorKind value) => value switch
    {
        UserTaskAnchorKind.Now => "now",
        UserTaskAnchorKind.At => "at",
        UserTaskAnchorKind.Window => "window",
        UserTaskAnchorKind.AnytimeToday => "anytimeToday",
        _ => "unspecified",
    };

    internal static UserTaskAnchorKind AnchorKindFromWire(string value) => value switch
    {
        "now" => UserTaskAnchorKind.Now,
        "at" => UserTaskAnchorKind.At,
        "window" => UserTaskAnchorKind.Window,
        "anytimeToday" => UserTaskAnchorKind.AnytimeToday,
        _ => UserTaskAnchorKind.Unspecified,
    };

    internal static string SourceKindToWire(UserTaskSourceKind value) => value switch
    {
        UserTaskSourceKind.Attention => "attention",
        UserTaskSourceKind.FailureIncident => "failureIncident",
        UserTaskSourceKind.Harvest => "harvest",
        UserTaskSourceKind.FilamentCoverage => "filamentCoverage",
        UserTaskSourceKind.Maintenance => "maintenance",
        UserTaskSourceKind.SpoolReorder => "spoolReorder",
        UserTaskSourceKind.PrintedPartStock => "printedPartStock",
        _ => "unspecified",
    };

    internal static UserTaskSourceKind SourceKindFromWire(string value) => value switch
    {
        "attention" => UserTaskSourceKind.Attention,
        "failureIncident" => UserTaskSourceKind.FailureIncident,
        "harvest" => UserTaskSourceKind.Harvest,
        "filamentCoverage" => UserTaskSourceKind.FilamentCoverage,
        "maintenance" => UserTaskSourceKind.Maintenance,
        "spoolReorder" => UserTaskSourceKind.SpoolReorder,
        "printedPartStock" => UserTaskSourceKind.PrintedPartStock,
        _ => UserTaskSourceKind.Unspecified,
    };
}
