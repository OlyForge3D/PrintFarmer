using Farm.Infrastructure.Services.HostUpdates;
using Npgsql;

namespace Farm.Web.Api.Tests.Services.HostUpdates;

/// <summary>
/// Issue #3177: the PostgreSQL rollback pre-clean must run against a real server. It has to drop
/// every object an interrupted migration could have created after the backup, while leaving the
/// <c>public</c> schema itself with the owner, ACL and comment it had before, because pg_dump
/// never emits those and pg_restore would otherwise not reproduce them.
/// </summary>
[Trait("Category", "Docker")]
public sealed class HostUpdatePostgresRestorePrepareProviderTests
{
    private const string PostgresConnectionVariable = "PFARM_TEST_POSTGRES_CONN";

    [Fact]
    public async Task Clear_drops_every_user_object_and_preserves_public_schema_metadata()
    {
        await using ScratchDatabase database = await ScratchDatabase.CreateAsync();
        await database.ExecuteAsync("""
            COMMENT ON SCHEMA public IS 'pf restore marker';
            GRANT USAGE, CREATE ON SCHEMA public TO PUBLIC;
            CREATE TABLE public.leftover_table (id integer PRIMARY KEY);
            INSERT INTO public.leftover_table VALUES (1);
            CREATE SEQUENCE public.leftover_sequence;
            CREATE TYPE public.leftover_type AS ENUM ('a');
            CREATE FUNCTION public.leftover_function() RETURNS integer LANGUAGE sql AS 'SELECT 1';
            CREATE VIEW public.leftover_view AS SELECT id FROM public.leftover_table;
            CREATE SCHEMA leftover_schema;
            CREATE TABLE leftover_schema.nested (id integer);
            CREATE SCHEMA "Mixed Case; Schema";
            CREATE TABLE "Mixed Case; Schema"."Quoted" (id integer);
            """);
        string publicBefore = await database.ScalarAsync(PublicSchemaMetadataSql);

        await HostUpdateDatabaseBackupTargetFactory.ClearPostgresDatabaseAsync(database.ConnectionString, CancellationToken.None);

        Assert.Equal(publicBefore, await database.ScalarAsync(PublicSchemaMetadataSql));
        Assert.Equal("public", await database.ScalarAsync("""
            SELECT string_agg(nspname, ',' ORDER BY nspname) FROM pg_namespace
             WHERE nspname NOT IN ('pg_catalog', 'information_schema')
               AND nspname NOT LIKE 'pg\_toast%' AND nspname NOT LIKE 'pg\_temp\_%'
            """));
        Assert.Equal("0", await database.ScalarAsync("""
            SELECT (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public')
                 + (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public')
                 + (SELECT count(*) FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = 'public')
            """));
    }

    [Fact]
    public async Task Clear_is_idempotent_on_an_already_empty_database()
    {
        await using ScratchDatabase database = await ScratchDatabase.CreateAsync();
        string publicBefore = await database.ScalarAsync(PublicSchemaMetadataSql);

        await HostUpdateDatabaseBackupTargetFactory.ClearPostgresDatabaseAsync(database.ConnectionString, CancellationToken.None);
        await HostUpdateDatabaseBackupTargetFactory.ClearPostgresDatabaseAsync(database.ConnectionString, CancellationToken.None);

        Assert.Equal(publicBefore, await database.ScalarAsync(PublicSchemaMetadataSql));
    }

    private const string PublicSchemaMetadataSql = """
        SELECT concat_ws('|', n.nspowner::regrole::text, coalesce(n.nspacl::text, '<null>'), coalesce(obj_description(n.oid, 'pg_namespace'), '<null>'))
          FROM pg_namespace n WHERE n.nspname = 'public'
        """;

    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private readonly string _serverConnectionString;
        private readonly string _name;

        private ScratchDatabase(string serverConnectionString, string name, string connectionString)
        {
            _serverConnectionString = serverConnectionString;
            _name = name;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<ScratchDatabase> CreateAsync()
        {
            string? serverConnectionString = Environment.GetEnvironmentVariable(PostgresConnectionVariable);
            Assert.False(string.IsNullOrWhiteSpace(serverConnectionString), $"postgres provider verification did not run: set {PostgresConnectionVariable}.");

            string name = "pf_prepare_" + Guid.NewGuid().ToString("N");
            string connectionString = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = name }.ConnectionString;
            await ExecuteOnAsync(serverConnectionString!, $"CREATE DATABASE \"{name}\"");
            return new ScratchDatabase(serverConnectionString!, name, connectionString);
        }

        public Task ExecuteAsync(string sql) => ExecuteOnAsync(ConnectionString, sql);

        public async Task<string> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnAsync(_serverConnectionString, $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
        }

        private static async Task ExecuteOnAsync(string connectionString, string sql)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
            _ = await command.ExecuteNonQueryAsync();
        }
    }
}
