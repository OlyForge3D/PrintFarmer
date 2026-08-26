using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Tests.Services.Gcode;

public sealed class PrinterModelAliasQueryTranslationTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void BuildMatchingAliasesQuery_AllProviders_UsesNormalizedIndexedColumns(
        string provider)
    {
        DbContextOptionsBuilder<AppDbContext> options = new();
        _ = provider switch
        {
            "sqlite" => options.UseSqlite("Data Source=:memory:"),
            "postgres" => options.UseNpgsql(
                "Host=localhost;Database=model_only;Username=model_only;Password=model_only"),
            "sqlserver" => options.UseSqlServer(
                "Server=(localdb)\\model_only;Database=model_only;Trusted_Connection=True"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        using var dbContext = new AppDbContext(options.Options);
        var service = new PrinterModelAliasService(dbContext);

        string sql = service
            .BuildMatchingAliasesQuery(
                " micron 180 ",
                " orcaslicer ",
                includeGeneric: true)
            .ToQueryString();

        sql.Should().Contain(nameof(PrinterModelAlias.SlicerModelNameNormalized));
        sql.Should().Contain(nameof(PrinterModelAlias.SlicerTypeNormalized));
        sql.Contains("upper(", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        dbContext.Model.FindEntityType(typeof(PrinterModelAlias))!
            .GetIndexes()
            .Should()
            .Contain(index =>
                index.GetDatabaseName() == "IX_PrinterModelAliases_NormalizedLookup");
    }
}
