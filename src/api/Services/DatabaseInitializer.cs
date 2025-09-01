using Farm.Web.Api.Data;

namespace Farm.Web.Api.Services;

/// <summary>
/// Handles database initialization with retry logic for resilient startup
/// </summary>
public class DatabaseInitializer
{
    private readonly AppDbContext _context;
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly DatabaseSeeder _seeder;

    public DatabaseInitializer(AppDbContext context, ILogger<DatabaseInitializer> logger, DatabaseSeeder seeder)
    {
        _context = context;
        _logger = logger;
        _seeder = seeder;
    }

    /// <summary>
    /// Initialize database with retry logic for container startup scenarios
    /// </summary>
    public async Task InitializeAsync(string dbProvider, int maxRetries = 10, int delaySeconds = 5)
    {
        _logger.LogInformation("[DB] Starting database initialization for provider: {DbProvider}", dbProvider);

        var retryCount = 0;
        Exception? lastException = null;

        while (retryCount < maxRetries)
        {
            try
            {
                // Test database connectivity first
                await _context.Database.CanConnectAsync();
                _logger.LogInformation("[DB] Database connection established successfully");

                // Perform migrations or ensure creation
                try
                {
                    // For MVP development, use EnsureCreated instead of migrations
                    // This simplifies schema changes during rapid development
                    await _context.Database.EnsureCreatedAsync();
                    _logger.LogInformation("[DB] Database schema creation completed successfully");

                    // Commented out migration code for future use
                    // await _context.Database.MigrateAsync();
                    // _logger.LogInformation("[DB] Database migration completed successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DB] Database creation failed: {Message}", ex.Message);
                    throw; // Re-throw to trigger retry mechanism
                }

                // Seed catalog data
                await _seeder.SeedAllAsync();
                _logger.LogInformation("[DB] Database initialization completed successfully");

                return; // Success - exit retry loop
            }
            catch (Exception ex)
            {
                lastException = ex;
                retryCount++;

                if (retryCount < maxRetries)
                {
                    _logger.LogWarning(ex,
                        "[DB] Database initialization attempt {RetryCount}/{MaxRetries} failed: {Message}. Retrying in {Delay} seconds...",
                        retryCount, maxRetries, ex.Message, delaySeconds);

                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                }
                else
                {
                    _logger.LogError(ex,
                        "[DB] Database initialization failed after {MaxRetries} attempts. Last error: {Message}",
                        maxRetries, ex.Message);

                    throw new InvalidOperationException(
                        $"Failed to initialize database after {maxRetries} attempts. " +
                        $"This usually indicates the database server is not ready or connection settings are incorrect. " +
                        $"Last error: {ex.Message}", ex);
                }
            }
        }
    }

    /// <summary>
    /// Validate database connection without initializing
    /// </summary>
    public async Task<bool> ValidateConnectionAsync()
    {
        try
        {
            return await _context.Database.CanConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DB] Database connection validation failed: {Message}", ex.Message);
            return false;
        }
    }
}
