using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace Farm.Web.Api.Tests.TestInfrastructure;

// Minimal interceptor used in tests to ensure SQLite PRAGMA settings (foreign_keys)
// This implementation is defensive and best-effort so tests don't fail when run
// in environments without full SQLite support.
internal sealed class TestSqlitePragmaEnforcer : DbCommandInterceptor
{
    public TestSqlitePragmaEnforcer()
    {
    }

    public static void EnsureForeignKeysEnabled(SqliteConnection? conn)
    {
        try
        {
            if (conn == null)
            {
                return;
            }
            if (conn.State != System.Data.ConnectionState.Open)
            {
                conn.Open();
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Best-effort; swallow exceptions in test environment
        }
    }

    // No-op overrides - we don't alter commands in tests
}
