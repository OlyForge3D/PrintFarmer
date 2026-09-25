using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateDatabaseServerIdentityTests
{
    [Theory]
    [InlineData("Data Source=stable;Server=target-a;Initial Catalog=farm", "Data Source=stable;Server=target-b;Initial Catalog=farm")]
    [InlineData("Server=target-a;Address=stable;Database=farm", "Server=target-a;Address=other;Database=farm")]
    [InlineData("Server=target-a;Initial Catalog=farm;Database=farm", "Server=target-a;Initial Catalog=farm;Database=other")]
    [InlineData("Server=target-a,1433;Database=farm", "Server=target-a,1444;Database=farm")]
    public void SqlServer_change_to_any_server_or_catalog_alias_changes_the_identity(string authorized, string retargeted)
    {
        Identity("sqlserver", authorized).Should().NotBe(Identity("sqlserver", retargeted));
    }

    [Fact]
    public void SqlServer_identity_ignores_credentials_and_casing()
    {
        string first = Identity("sqlserver", "Server=SQL.Example;Database=farm;User Id=pf;Password=one");
        string second = Identity("sqlserver", "server=sql.example;database=farm;User Id=other;Password=two");

        first.Should().Be(second);
        first.Should().NotContain("one").And.NotContain("pf");
    }

    [Theory]
    [InlineData("Host=a.example;Port=5432;Database=farm", "Host=b.example;Port=5432;Database=farm")]
    [InlineData("Host=a.example;Port=5432;Database=farm", "Host=a.example;Port=6543;Database=farm")]
    [InlineData("Host=a.example;Port=5432;Database=farm", "Host=a.example;Port=5432;Database=other")]
    public void Postgres_host_port_or_database_change_changes_the_identity(string authorized, string retargeted)
    {
        Identity("postgres", authorized).Should().NotBe(Identity("postgres", retargeted));
    }

    [Theory]
    [InlineData("postgres", "Data Source=farm.db")]
    [InlineData("sqlserver", "Server=\"unterminated")]
    public void Unparseable_connection_strings_fail_closed(string provider, string connectionString)
    {
        Identity(provider, connectionString).Should().Be("unparseable");
    }

    private static string Identity(string provider, string connectionString) =>
        HostUpdateBaselineHashes.DatabaseServerIdentity(
            new DatabaseProviderConfiguration { Provider = provider, ConnectionString = connectionString })!;
}
