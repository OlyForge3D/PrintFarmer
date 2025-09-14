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

                    // Lightweight self-healing for SQLite when schema was created before introducing shadow columns
                    // Ensure case-insensitive shadow columns (NameLowered) & indexes exist for Manufacturers / PrinterModels.
                    if (string.Equals(dbProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            await EnsureCaseInsensitiveColumnsAsync();
                        }
                        catch (Exception colEx)
                        {
                            _logger.LogWarning(colEx, "[DB] Non-fatal: automatic shadow column/index verification failed: {Message}", colEx.Message);
                        }
                    }
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

                            if (string.Equals(dbProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    await EnsureCaseInsensitiveColumnsAsync();
                                }
                                catch (Exception colEx)
                                {
                                    _logger.LogWarning(colEx, "[DB] Non-fatal (fallback path): automatic shadow column/index verification failed: {Message}", colEx.Message);
                                }
                            }
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

    /// <summary>
    /// For SQLite + EnsureCreated dev workflow: if the database file predates new shadow columns
    /// (NameLowered) we add them and their unique indexes safely. This avoids forcing devs to delete
    /// the whole DB when only these columns were added for case-insensitive uniqueness.
    /// </summary>
    private async Task EnsureCaseInsensitiveColumnsAsync()
    {
        var conn = _context.Database.GetDbConnection();
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();
        try
        {
            async Task<bool> ColumnExistsAsync(string table, string column)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE lower(name)=lower(@col) LIMIT 1";
                var p = cmd.CreateParameter();
                p.ParameterName = "@col";
                p.Value = column;
                cmd.Parameters.Add(p);
                var result = await cmd.ExecuteScalarAsync();
                return result != null;
            }

            async Task EnsureColumnAsync(string table, string column)
            {
                if (!await ColumnExistsAsync(table, column))
                {
                    using var alter = conn.CreateCommand();
                    alter.Transaction = tx;
                    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT NOT NULL DEFAULT ''";
                    await alter.ExecuteNonQueryAsync();
                    _logger.LogInformation("[DB] Added missing column {Table}.{Column}", table, column);
                }
            }

            async Task<bool> HasDuplicatesAsync(string table)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                if (table == "Manufacturers")
                {
                    cmd.CommandText = "SELECT 1 FROM (SELECT lower(Name) AS L, COUNT(*) c FROM Manufacturers GROUP BY lower(Name) HAVING c>1) LIMIT 1";
                }
                else // PrinterModels uniqueness is per ManufacturerId + lower(Name)
                {
                    cmd.CommandText = "SELECT 1 FROM (SELECT ManufacturerId, lower(Name) AS L, COUNT(*) c FROM PrinterModels GROUP BY ManufacturerId, lower(Name) HAVING c>1) LIMIT 1";
                }
                var r = await cmd.ExecuteScalarAsync();
                return r != null;
            }

            async Task BackfillAsync(string table)
            {
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = $"UPDATE {table} SET NameLowered = lower(Name) WHERE NameLowered = '' OR NameLowered IS NULL";
                var rows = await upd.ExecuteNonQueryAsync();
                if (rows >= 0)
                {
                    _logger.LogDebug("[DB] Backfilled {Rows} rows for {Table}.NameLowered", rows, table);
                }
            }

            async Task EnsureIndexAsync(string sql, string description)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                try
                {
                    await cmd.ExecuteNonQueryAsync();
                    _logger.LogInformation("[DB] Ensured index: {Desc}", description);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DB] Failed to ensure index {Desc}: {Message}", description, ex.Message);
                }
            }

            // Manufacturers
            await EnsureColumnAsync("Manufacturers", "NameLowered");
            await BackfillAsync("Manufacturers");
            if (await HasDuplicatesAsync("Manufacturers"))
            {
                _logger.LogWarning("[DB] Duplicate manufacturer names (case-insensitive) detected; skipping unique index creation on Manufacturers.NameLowered. Resolve duplicates and restart to enforce uniqueness.");
            }
            else
            {
                await EnsureIndexAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_Manufacturers_NameLowered ON Manufacturers (NameLowered)", "IX_Manufacturers_NameLowered");
            }

            // PrinterModels
            await EnsureColumnAsync("PrinterModels", "NameLowered");
            await BackfillAsync("PrinterModels");
            if (await HasDuplicatesAsync("PrinterModels"))
            {
                _logger.LogWarning("[DB] Duplicate printer model names (case-insensitive within manufacturer) detected; skipping unique composite index creation. Resolve duplicates and restart to enforce uniqueness.");
            }
            else
            {
                await EnsureIndexAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_PrinterModels_ManufacturerId_NameLowered ON PrinterModels (ManufacturerId, NameLowered)", "IX_PrinterModels_ManufacturerId_NameLowered");
            }

            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("Failed to ensure shadow columns for case-insensitive uniqueness", ex);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
