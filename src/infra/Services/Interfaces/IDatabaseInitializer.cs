using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.Interfaces;

/// <summary>
/// Service for initializing the database schema and seeding initial data.
/// </summary>
public interface IDatabaseInitializer
{
    /// <summary>Initializes the database with retry logic for containerized environments.</summary>
    Task InitializeAsync(string dbProvider, int maxRetries = 10, int delaySeconds = 5);

    /// <summary>Seeds all initial data from YAML files.</summary>
    Task SeedAllAsync();
}
