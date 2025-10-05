namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Interface for database seeder providing initial data seeding functionality.
/// Handles populating the database with default manufacturers, models, and configuration data
/// when the application starts for the first time or when specific data is missing.
/// </summary>
public interface IDatabaseSeeder
{
    /// <summary>
    /// Performs complete database seeding including both catalog data and Spoolman configuration.
    /// This is a convenience method that calls both SeedCatalogDataAsync and SeedSpoolmanConfigAsync.
    /// </summary>
    /// <returns>A task that completes when all database seeding operations are finished</returns>
    Task SeedAllAsync();
}
