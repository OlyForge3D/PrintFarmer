namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Interface for database seeder providing initial data seeding functionality.
/// Handles populating the database with default manufacturers, models, and configuration data
/// when the application starts for the first time or when specific data is missing.
/// </summary>
public interface IDatabaseSeeder
{
    /// <summary>
    /// Seeds the database with catalog data including default printer manufacturers and their models.
    /// This includes popular 3D printer brands like Prusa, Ender, Bambu Lab, etc., with their specific models
    /// and build volume specifications.
    /// </summary>
    /// <returns>A task that completes when the catalog data seeding is finished</returns>
    Task SeedCatalogDataAsync();

    /// <summary>
    /// Seeds the database with default Spoolman configuration if none exists.
    /// Creates initial configuration entries for filament spool management integration.
    /// </summary>
    /// <returns>A task that completes when the Spoolman configuration seeding is finished</returns>
    Task SeedSpoolmanConfigAsync();

    /// <summary>
    /// Performs complete database seeding including both catalog data and Spoolman configuration.
    /// This is a convenience method that calls both SeedCatalogDataAsync and SeedSpoolmanConfigAsync.
    /// </summary>
    /// <returns>A task that completes when all database seeding operations are finished</returns>
    Task SeedAllAsync();
}
