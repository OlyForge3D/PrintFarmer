using Microsoft.Extensions.Configuration;

namespace Farm.Infrastructure.Data;

/// <summary>
/// Resolved database provider configuration. Centralizes the logic for reading
/// DB_PROVIDER and connection string values from <see cref="IConfiguration"/> so
/// that both the API's <c>AppDbContext</c> and the slicer module's <c>SlicerDbContext</c>
/// resolve their provider settings identically.
/// </summary>
public sealed record DatabaseProviderConfiguration
{
    /// <summary>
    /// Normalized provider name: "sqlite", "sqlserver", "postgres", or "postgresql".
    /// </summary>
    public string Provider { get; init; } = "sqlite";

    /// <summary>
    /// Resolved connection string for the selected provider.
    /// </summary>
    public string ConnectionString { get; init; } = "Data Source=farm.db";

    /// <summary>
    /// Whether the provider is SQLite.
    /// </summary>
    public bool IsSqlite => Provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the provider is SQL Server.
    /// </summary>
    public bool IsSqlServer => Provider.Equals("sqlserver", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the provider is PostgreSQL (accepts both "postgres" and "postgresql").
    /// </summary>
    public bool IsPostgres => Provider.Equals("postgres", StringComparison.OrdinalIgnoreCase)
                           || Provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads <c>DB_PROVIDER</c> and <c>ConnectionStrings:Default</c> (or <c>DB_CONNECTION</c>)
    /// from the supplied <paramref name="configuration"/> and returns a resolved
    /// <see cref="DatabaseProviderConfiguration"/>.
    /// </summary>
    public static DatabaseProviderConfiguration FromConfiguration(IConfiguration configuration)
    {
        string? providerRaw = configuration.GetValue<string>("DB_PROVIDER");
        string provider = string.IsNullOrWhiteSpace(providerRaw) ? "sqlite" : providerRaw.Trim();

        string connectionString = configuration.GetConnectionString("Default")
            ?? configuration.GetValue<string>("DB_CONNECTION")
            ?? "Data Source=farm.db";

        return new DatabaseProviderConfiguration
        {
            Provider = provider,
            ConnectionString = connectionString
        };
    }

    /// <summary>
    /// Name of the optional connection string that points <c>SlicerDbContext</c> at a database
    /// separate from <c>AppDbContext</c>.
    /// </summary>
    public const string SlicerDatabaseConnectionStringName = "SlicerDatabase";

    /// <summary>
    /// Resolves the configuration for <c>SlicerDbContext</c>. Uses the same provider as
    /// <see cref="FromConfiguration"/>, and uses <c>ConnectionStrings:SlicerDatabase</c> when it is
    /// set; otherwise the slicer shares the application connection string.
    /// </summary>
    /// <remarks>
    /// A distinct slicer database is honoured so split deployments keep their application and
    /// slicer storage boundaries explicit.
    /// </remarks>
    public static DatabaseProviderConfiguration ForSlicerDatabase(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        DatabaseProviderConfiguration shared = FromConfiguration(configuration);
        string? slicerConnectionString = configuration.GetConnectionString(SlicerDatabaseConnectionStringName);

        return string.IsNullOrWhiteSpace(slicerConnectionString)
            ? shared
            : shared with { ConnectionString = slicerConnectionString };
    }
}
