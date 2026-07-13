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
///   <item><description><c>IX_UserTasks_SourceKind_SourceId</c> — dedupe/lookup for the
///   compiler. Declared in <see cref="Farm.Infrastructure.Data.AppDbContext.OnModelCreating"/>
///   because it is a provider-aware <em>unique filtered</em> index (unique on
///   <c>(SourceKind, SourceId)</c> among open rows where <c>SourceId IS NOT NULL AND
///   Status IN (Pending, InProgress)</c>). The filter both guarantees at most one open
///   compiler task per source (issue #713 Fix E) and covers the compiler's
///   <c>GetOpenBySourceAsync</c> lookup (Fix I).</description></item>
///   <item><description><c>IX_UserTasks_Status_AnchorKind_AnchorAtUtc</c> — supports the shift-plan grouped/ordered query.</description></item>
///   <item><description><c>IX_UserTasks_Status_UpdatedAt</c> — supports suppressed-key bootstrap and recently-updated task lookbacks.</description></item>
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

        // NOTE: the (SourceKind, SourceId) dedupe index is a provider-aware unique
        // filtered index declared in AppDbContext.OnModelCreating (Fix E/I) — it cannot
        // live here because the filter SQL differs per provider.

        // Shift-plan grouped/ordered query support.
        _ = builder.HasIndex(t => new { t.Status, t.AnchorKind, t.AnchorAtUtc })
            .HasDatabaseName("IX_UserTasks_Status_AnchorKind_AnchorAtUtc");

        // Supports suppression lookbacks and other recent-by-status task queries.
        _ = builder.HasIndex(t => new { t.Status, t.UpdatedAt })
            .HasDatabaseName("IX_UserTasks_Status_UpdatedAt");
    }

    // Canonical wire values. Delegates to the domain converters so EF and JSON stay in lockstep.
    internal static string AnchorKindToWire(UserTaskAnchorKind value)
        => UserTaskAnchorKindJsonConverter.ToWire(value);

    internal static UserTaskAnchorKind AnchorKindFromWire(string value)
        => UserTaskAnchorKindJsonConverter.FromWire(value);

    internal static string SourceKindToWire(UserTaskSourceKind value)
        => UserTaskSourceKindJsonConverter.ToWire(value);

    internal static UserTaskSourceKind SourceKindFromWire(string value)
        => UserTaskSourceKindJsonConverter.FromWire(value);
}
