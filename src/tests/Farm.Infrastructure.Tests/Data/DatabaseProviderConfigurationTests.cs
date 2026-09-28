using Farm.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Farm.Infrastructure.Tests.Data;

public sealed class DatabaseProviderConfigurationTests
{
    private static IConfiguration Configuration(string? slicerDatabase) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PROVIDER"] = "postgres",
                ["ConnectionStrings:Default"] = "Host=db;Database=printfarmer",
                ["ConnectionStrings:SlicerDatabase"] = slicerDatabase,
            })
            .Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForSlicerDatabase_WithoutSlicerDatabase_SharesApplicationConnectionString(string? slicerDatabase)
    {
        DatabaseProviderConfiguration slicer = DatabaseProviderConfiguration.ForSlicerDatabase(Configuration(slicerDatabase));

        slicer.Should().Be(DatabaseProviderConfiguration.FromConfiguration(Configuration(slicerDatabase)));
        slicer.ConnectionString.Should().Be("Host=db;Database=printfarmer");
    }

    [Fact]
    public void ForSlicerDatabase_WithSlicerDatabase_UsesDistinctConnectionStringAndSameProvider()
    {
        DatabaseProviderConfiguration slicer = DatabaseProviderConfiguration.ForSlicerDatabase(
            Configuration("Host=db;Database=printfarmer_slicer"));

        slicer.Provider.Should().Be("postgres");
        slicer.IsPostgres.Should().BeTrue();
        slicer.ConnectionString.Should().Be("Host=db;Database=printfarmer_slicer");
        DatabaseProviderConfiguration.FromConfiguration(Configuration("Host=db;Database=printfarmer_slicer"))
            .ConnectionString.Should().Be("Host=db;Database=printfarmer");
    }
}
