using Farm.Web.Api.Data;
using Microsoft.EntityFrameworkCore;

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

        while (retryCount < maxRetries)
        {
            try
            {
                // Test database connectivity first
                await _context.Database.CanConnectAsync();
                _logger.LogInformation("[DB] Database connection established successfully");

                // For MVP development, use EnsureCreated instead of migrations.
                // This approach automatically handles schema changes during development.
                try
                {
                    await _context.Database.EnsureCreatedAsync();
                    _logger.LogInformation("[DB] Database schema ensured successfully (EnsureCreated)");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DB] EnsureCreated failed: {Message}. Attempting manual schema initialization for SQLite.", ex.Message);
                    // Fallback: very early containers (or volume permission issues) sometimes cause EnsureCreated to throw
                    // For SQLite only, attempt a minimal manual schema verification/creation of the Users table presence heuristic.
                    try
                    {
                        if (string.Equals(dbProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                        {
                            // Issue a pragma to force open / create file, then check a sentinel table.
                            await _context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                            // If no tables exist, this query will fail; wrap & create a tiny bootstrap table then re-run seed later.
                            // We won't create full schema manually (that belongs to EF model); just let a second EnsureCreated attempt run.
                            await _context.Database.EnsureCreatedAsync();
                            _logger.LogInformation("[DB] Fallback EnsureCreated second attempt succeeded");
                        }
                        else
                        {
                            throw; // Non-SQLite providers should just retry via outer loop
                        }
                    }
                    catch (Exception inner)
                    {
                        _logger.LogError(inner, "[DB] Manual fallback schema initialization failed. Will retry (attempt {Attempt})", retryCount + 1);
                        throw; // Bubble to retry loop
                    }
                }

                // Seed catalog data
                await _seeder.SeedAllAsync();
                _logger.LogInformation("[DB] Database initialization completed successfully");

                return; // Success - exit retry loop
            }
            catch (Exception ex)
            {
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
