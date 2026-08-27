using System.Data.Common;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Statistics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Infrastructure.Tests.Services.Statistics;

[Collection(ProviderDatabaseTestCollection.Name)]
public class PredictiveAnalyticsProviderTests(ITestOutputHelper output)
{
    private const string PostgresConnEnvVar = "PFARM_TEST_POSTGRES_CONN";
    private const string SqlServerConnEnvVar = "PFARM_TEST_SQLSERVER_CONN";
    private readonly ITestOutputHelper _output = output;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PostgreSQL_GetActiveAlertsAsync_ExecutesGroupedTrendQuery()
    {
        string connectionString = GetRequiredConnectionString(PostgresConnEnvVar, "PostgreSQL");
        var interceptor = new PrintJobCommandInterceptor();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                connectionString,
                provider => provider.MigrationsAssembly("Farm.Migrations.PostgreSQL"))
            .AddInterceptors(interceptor)
            .Options;

        await RunProviderAssertionsAsync(options, interceptor, "PostgreSQL");
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SqlServer_GetActiveAlertsAsync_ExecutesGroupedTrendQuery()
    {
        string connectionString = GetRequiredConnectionString(SqlServerConnEnvVar, "SQL Server");
        var interceptor = new PrintJobCommandInterceptor();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(
                connectionString,
                provider => provider.MigrationsAssembly("Farm.Migrations.SqlServer"))
            .AddInterceptors(interceptor)
            .Options;

        await RunProviderAssertionsAsync(options, interceptor, "SQL Server");
    }

    private async Task RunProviderAssertionsAsync(
        DbContextOptions<AppDbContext> options,
        PrintJobCommandInterceptor interceptor,
        string providerName)
    {
        await using var db = new AppDbContext(options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        try
        {
            var manufacturer = new Manufacturer { Name = $"{providerName} Trend Mfg" };
            db.Manufacturers.Add(manufacturer);
            await db.SaveChangesAsync();

            var model = new PrinterModel
            {
                Name = $"{providerName} Trend Model",
                ManufacturerId = manufacturer.Id
            };
            db.PrinterModels.Add(model);
            await db.SaveChangesAsync();

            var decliningPrinter = CreatePrinter(
                $"{providerName} Declining",
                $"http://{providerName.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant()}-declining.local",
                manufacturer.Id,
                model.Id);
            var stablePrinter = CreatePrinter(
                $"{providerName} Stable",
                $"http://{providerName.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant()}-stable.local",
                manufacturer.Id,
                model.Id);
            var noHistoryPrinter = CreatePrinter(
                $"{providerName} No History",
                $"http://{providerName.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant()}-no-history.local",
                manufacturer.Id,
                model.Id);
            db.Printers.AddRange(decliningPrinter, stablePrinter, noHistoryPrinter);
            await db.SaveChangesAsync();

            var now = DateTime.UtcNow;
            for (int i = 0; i < 4; i++)
            {
                db.PrintJobs.Add(CreateJob(decliningPrinter.Id, PrintJobStatus.Completed, now.AddDays(-10)));
                db.PrintJobs.Add(CreateJob(stablePrinter.Id, PrintJobStatus.Completed, now.AddDays(-10)));
                db.PrintJobs.Add(CreateJob(stablePrinter.Id, PrintJobStatus.Completed, now.AddDays(-2)));
            }

            db.PrintJobs.Add(CreateJob(decliningPrinter.Id, PrintJobStatus.Completed, now.AddDays(-2)));
            for (int i = 0; i < 3; i++)
            {
                db.PrintJobs.Add(CreateJob(decliningPrinter.Id, PrintJobStatus.Failed, now.AddDays(-2)));
            }

            await db.SaveChangesAsync();

            var service = new PredictiveAnalyticsService(db);
            interceptor.Reset();

            var result = await service.GetActiveAlertsAsync();

            result.Where(alert => alert.AlertType == "DecliningPerformance")
                .Should()
                .ContainSingle(alert => alert.Message.Contains(decliningPrinter.Name, StringComparison.Ordinal));

            string groupedSql = interceptor.PrintJobCommands
                .Single(command => command.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase));
            groupedSql.Should().ContainEquivalentOf("count(");
            if (providerName == "PostgreSQL")
            {
                groupedSql.Should().Contain("FILTER (WHERE", Exactly.Times(4));
            }
            else
            {
                groupedSql.Should().Contain("COUNT(CASE", Exactly.Times(4));
            }

            _output.WriteLine($"{providerName} grouped trend SQL:{Environment.NewLine}{groupedSql}");
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static string GetRequiredConnectionString(string environmentVariable, string providerName)
    {
        string? connectionString = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Fail(
                $"{providerName} predictive analytics verification DID NOT RUN: " +
                $"set {environmentVariable} to a live provider connection string.");
        }

        return connectionString;
    }

    private static Printer CreatePrinter(
        string name,
        string serverUrl,
        Guid manufacturerId,
        Guid modelId) =>
        new()
        {
            Name = name,
            ServerUrl = serverUrl,
            BackendPort = 7125,
            ModelId = modelId,
            ManufacturerId = manufacturerId,
            Backend = (int)PrinterBackend.Moonraker
        };

    private static PrintJob CreateJob(Guid printerId, PrintJobStatus status, DateTime queuedAt) =>
        new()
        {
            Name = $"{status}-{Guid.NewGuid()}",
            AssignedPrinterId = printerId,
            QueuedAt = queuedAt,
            Status = status
        };

    private sealed class PrintJobCommandInterceptor : DbCommandInterceptor
    {
        private readonly Lock _sync = new();
        private readonly List<string> _printJobCommands = [];

        public IReadOnlyList<string> PrintJobCommands
        {
            get
            {
                lock (_sync)
                {
                    return [.. _printJobCommands];
                }
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _printJobCommands.Clear();
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("PrintJobs", StringComparison.Ordinal))
            {
                lock (_sync)
                {
                    _printJobCommands.Add(command.CommandText);
                }
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
