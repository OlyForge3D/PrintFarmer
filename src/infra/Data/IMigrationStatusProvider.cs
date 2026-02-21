namespace Farm.Infrastructure.Data;

/// <summary>
/// Provides database migration status information.
/// </summary>
public interface IMigrationStatusProvider
{
    /// <summary>Gets the current migration status of the database.</summary>
    MigrationStatus GetStatus();
}
