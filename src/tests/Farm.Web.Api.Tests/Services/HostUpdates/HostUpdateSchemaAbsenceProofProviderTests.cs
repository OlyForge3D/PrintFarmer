using System.Data.Common;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Farm.Web.Api.Tests.Services.HostUpdates;

/// <summary>
/// Issue #3141: the fresh-host schema-absence proof must be a real catalog check on the server
/// providers. Npgsql's <c>IHistoryRepository.ExistsAsync</c> always reports true, which made an
/// empty PostgreSQL database look migrated and sent host-update reads at absent tables. Each test
/// runs in its own scratch database created from the CI provider connection, so empty, partially
/// migrated, and fully migrated states are all real server catalogs.
/// </summary>
[Trait("Category", "Docker")]
public sealed class HostUpdateSchemaAbsenceProofProviderTests
{
    private const string PostgresConnectionVariable = "PFARM_TEST_POSTGRES_CONN";
    private const string SqlServerConnectionVariable = "PFARM_TEST_SQLSERVER_CONN";

    private static readonly Type[] AppEntities = [typeof(PrintJob), typeof(QueueDispatchOutbox)];
    private static readonly Type[] SlicerRegistrationEntities = [typeof(SlicerService)];
    private static readonly Type[] SlicerWorkEntities = [typeof(SliceJob)];

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public async Task Empty_database_is_never_migrated_for_both_contexts_and_every_caller_sees_no_work(string provider)
    {
        await using ScratchDatabase database = await ScratchDatabase.CreateAsync(provider);
        await using AppDbContext app = database.CreateAppContext();
        await using SlicerDbContext slicer = database.CreateSlicerContext();

        Assert.True(await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(app, AppEntities, CancellationToken.None));
        Assert.True(await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(slicer, SlicerRegistrationEntities, CancellationToken.None));
        Assert.True(await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(slicer, SlicerWorkEntities, CancellationToken.None));

        Assert.Equal(0, await new DbActiveWorkObservationPort(app).CountActiveAsync(CancellationToken.None));
        Assert.Equal(0, await new SlicerActiveWorkObservationPort(slicer).CountActiveAsync(CancellationToken.None));
        HostUpdatePrinterCommandInventory inventory = await new DbHostUpdatePrinterCommandInventoryReader(app).ReadAsync(CancellationToken.None);
        Assert.Empty(inventory.Printers);
        Assert.Equal(0, inventory.UncertainOutcomeCount);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public async Task History_table_without_work_tables_is_damaged_and_the_read_fails_closed(string provider)
    {
        await using ScratchDatabase database = await ScratchDatabase.CreateAsync(provider);
        await using (SlicerDbContext seed = database.CreateSlicerContext())
        {
            _ = await seed.Database.ExecuteSqlRawAsync(seed.GetService<IHistoryRepository>().GetCreateScript());
        }

        await using SlicerDbContext slicer = database.CreateSlicerContext();

        Assert.False(await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(slicer, SlicerWorkEntities, CancellationToken.None));
        _ = await Assert.ThrowsAnyAsync<DbException>(
            () => new SlicerActiveWorkObservationPort(slicer).CountActiveAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public async Task Mapped_table_without_history_is_not_a_fresh_host(string provider)
    {
        await using ScratchDatabase database = await ScratchDatabase.CreateAsync(provider);
        await database.ExecuteAsync(provider == "postgres"
            ? "CREATE SCHEMA slicer; CREATE TABLE slicer.\"SlicerServices\" (x integer);"
            : "EXEC('CREATE SCHEMA slicer'); CREATE TABLE slicer.[SlicerServices] (x int);");
        await using SlicerDbContext slicer = database.CreateSlicerContext();

        Assert.False(await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(slicer, SlicerRegistrationEntities, CancellationToken.None));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public async Task Partially_migrated_database_is_not_a_fresh_host(string provider)
    {
        await using ScratchDatabase database = await ScratchDatabase.CreateAsync(provider);
        await using (AppDbContext seed = database.CreateAppContext())
        {
            string first = seed.Database.GetMigrations().First();
            await seed.GetService<IMigrator>().MigrateAsync(first);
        }

        await using AppDbContext app = database.CreateAppContext();
        Assert.NotEmpty(await app.Database.GetAppliedMigrationsAsync());
        Assert.NotEmpty(await app.Database.GetPendingMigrationsAsync());

        Assert.False(await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(app, AppEntities, CancellationToken.None));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public async Task Fully_migrated_database_is_not_a_fresh_host_and_callers_read_the_real_tables(string provider)
    {
        await using ScratchDatabase database = await ScratchDatabase.CreateAsync(provider);
        await using (AppDbContext seedApp = database.CreateAppContext())
        {
            await seedApp.Database.MigrateAsync();
        }

        await using (SlicerDbContext seedSlicer = database.CreateSlicerContext())
        {
            await seedSlicer.Database.MigrateAsync();
        }

        await using AppDbContext app = database.CreateAppContext();
        await using SlicerDbContext slicer = database.CreateSlicerContext();

        Assert.False(await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(app, AppEntities, CancellationToken.None));
        Assert.False(await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(slicer, SlicerRegistrationEntities, CancellationToken.None));
        Assert.False(await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(slicer, SlicerWorkEntities, CancellationToken.None));

        Assert.Equal(0, await new DbActiveWorkObservationPort(app).CountActiveAsync(CancellationToken.None));
        Assert.Equal(0, await new SlicerActiveWorkObservationPort(slicer).CountActiveAsync(CancellationToken.None));
        Assert.Empty(await slicer.SlicerServices.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task PostgreSql_table_on_a_non_default_search_path_schema_is_present()
    {
        // An unqualified query resolves through the whole search_path, not only current_schema().
        await using ScratchDatabase database = await ScratchDatabase.CreateAsync("postgres");
        await database.ExecuteAsync("CREATE SCHEMA pf_other; CREATE TABLE pf_other.\"PrintJobs\" (x integer);");
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString) { SearchPath = "public,pf_other" };
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(builder.ConnectionString, x => x.MigrationsAssembly("Farm.Migrations.PostgreSQL"))
            .Options;
        await using var app = new AppDbContext(options);

        Assert.False(await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(app, [typeof(PrintJob)], CancellationToken.None));
    }

    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private readonly string _provider;
        private readonly string _serverConnectionString;
        private readonly string _name;

        private ScratchDatabase(string provider, string serverConnectionString, string name, string connectionString)
        {
            _provider = provider;
            _serverConnectionString = serverConnectionString;
            _name = name;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<ScratchDatabase> CreateAsync(string provider)
        {
            string variable = provider == "postgres" ? PostgresConnectionVariable : SqlServerConnectionVariable;
            string? serverConnectionString = Environment.GetEnvironmentVariable(variable);
            Assert.False(string.IsNullOrWhiteSpace(serverConnectionString), $"{provider} provider verification did not run: set {variable}.");

            string name = "pf_absence_" + Guid.NewGuid().ToString("N");
            string connectionString;
            if (provider == "postgres")
            {
                connectionString = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = name }.ConnectionString;
                await ExecuteOnAsync(new NpgsqlConnection(serverConnectionString), $"CREATE DATABASE \"{name}\"");
            }
            else
            {
                connectionString = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = name }.ConnectionString;
                await ExecuteOnAsync(new SqlConnection(serverConnectionString), $"CREATE DATABASE [{name}]");
            }

            return new ScratchDatabase(provider, serverConnectionString!, name, connectionString);
        }

        public AppDbContext CreateAppContext()
        {
            var builder = new DbContextOptionsBuilder<AppDbContext>();
            _ = _provider == "postgres"
                ? builder.UseNpgsql(ConnectionString, x => x.MigrationsAssembly("Farm.Migrations.PostgreSQL"))
                : builder.UseSqlServer(ConnectionString, x => x.MigrationsAssembly("Farm.Migrations.SqlServer"));
            return new AppDbContext(builder.Options);
        }

        public SlicerDbContext CreateSlicerContext()
        {
            var builder = new DbContextOptionsBuilder<SlicerDbContext>();
            _ = _provider == "postgres"
                ? builder.UseNpgsql(ConnectionString, x => x.MigrationsAssembly("Farm.Slicer.Migrations.PostgreSQL"))
                : builder.UseSqlServer(ConnectionString, x => x.MigrationsAssembly("Farm.Slicer.Migrations.SqlServer"));
            return new SlicerDbContext(builder.Options);
        }

        public Task ExecuteAsync(string sql) => ExecuteOnAsync(
            _provider == "postgres" ? new NpgsqlConnection(ConnectionString) : new SqlConnection(ConnectionString),
            sql);

        public async ValueTask DisposeAsync()
        {
            if (_provider == "postgres")
            {
                NpgsqlConnection.ClearAllPools();
                await ExecuteOnAsync(new NpgsqlConnection(_serverConnectionString), $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
            }
            else
            {
                SqlConnection.ClearAllPools();
                await ExecuteOnAsync(
                    new SqlConnection(_serverConnectionString),
                    $"IF DB_ID(N'{_name}') IS NOT NULL BEGIN ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}]; END");
            }
        }

        private static async Task ExecuteOnAsync(DbConnection connection, string sql)
        {
            await using (connection)
            {
                await connection.OpenAsync();
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = 120;
                _ = await command.ExecuteNonQueryAsync();
            }
        }
    }
}
