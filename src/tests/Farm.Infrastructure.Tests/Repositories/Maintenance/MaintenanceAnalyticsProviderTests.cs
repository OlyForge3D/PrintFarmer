using System.Data.Common;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Infrastructure.Tests.Repositories.Maintenance;

[Collection(ProviderDatabaseTestCollection.Name)]
public sealed class MaintenanceAnalyticsProviderTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Analytics_SQLite_PreservesResultsWithoutMaterializingEntities()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var capture = new AnalyticsCapture();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).AddInterceptors(capture).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await AssertAnalyticsAsync(db, capture);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Analytics_PostgreSQL_PreservesResultsWithoutMaterializingEntities()
    {
        var builder = new NpgsqlConnectionStringBuilder(RequiredConnection("PFARM_TEST_POSTGRES_CONN"))
        {
            Database = $"maintenance_analytics_{Guid.NewGuid():N}"
        };
        var capture = new AnalyticsCapture();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(builder.ConnectionString, p => p.MigrationsAssembly("Farm.Migrations.PostgreSQL"))
            .AddInterceptors(capture).Options;
        await AssertIsolatedProviderAsync(options, capture);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Analytics_SqlServer_PreservesResultsWithoutMaterializingEntities()
    {
        var builder = new SqlConnectionStringBuilder(RequiredConnection("PFARM_TEST_SQLSERVER_CONN"))
        {
            InitialCatalog = $"maintenance_analytics_{Guid.NewGuid():N}"
        };
        var capture = new AnalyticsCapture();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(builder.ConnectionString, p => p.MigrationsAssembly("Farm.Migrations.SqlServer"))
            .AddInterceptors(capture).Options;
        await AssertIsolatedProviderAsync(options, capture);
    }

    private async Task AssertIsolatedProviderAsync(DbContextOptions<AppDbContext> options, AnalyticsCapture capture)
    {
        // Never delete or seed the database named in the supplied connection string.
        await using var db = new AppDbContext(options);
        try
        {
            await db.Database.MigrateAsync();
            await AssertAnalyticsAsync(db, capture);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static string RequiredConnection(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        Assert.False(string.IsNullOrWhiteSpace(value),
            $"Maintenance analytics provider execution DID NOT RUN: set {variable} to a live server with database creation permission.");
        return value!;
    }

    private async Task AssertAnalyticsAsync(AppDbContext db, AnalyticsCapture capture)
    {
        var repo = new EfMaintenanceLogRepository(db);
        (await repo.GetCostAnalysisAsync()).Should().BeEmpty();
        (await repo.GetPrinterUptimeAsync()).Should().BeEmpty();
        (await repo.GetComponentLifespanAsync()).Should().BeEmpty();

        var manufacturer = new Manufacturer { Name = "Analytics Mfg" };
        var model = new PrinterModel { Name = "Analytics Model", Manufacturer = manufacturer };
        var active = NewPrinter("Active", model, manufacturer);
        var noStatistics = NewPrinter("No statistics", model, manufacturer);
        var noHistory = NewPrinter("No history", model, manufacturer);
        var zeroHours = NewPrinter("Zero hours", model, manufacturer);
        var negativeDowntime = NewPrinter("Negative downtime", model, manufacturer);
        active.Statistics = new PrinterStatistics { PrinterId = active.Id, TotalPrintHours = 10 };
        zeroHours.Statistics = new PrinterStatistics { PrinterId = zeroHours.Id, TotalPrintHours = 0 };
        negativeDowntime.Statistics = new PrinterStatistics { PrinterId = negativeDowntime.Id, TotalPrintHours = 10 };
        var toolhead = new Toolhead
        {
            PrinterId = active.Id,
            Name = "T0",
            Index = 0,
            IsPrimary = true,
            ToolheadType = ToolheadType.Physical,
            UpdatedAt = DateTime.UtcNow
        };
        active.Toolheads.Add(toolhead);
        db.Printers.AddRange(active, noStatistics, noHistory, zeroHours, negativeDowntime);
        await db.SaveChangesAsync();

        DateTime now = DateTime.UtcNow;
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        DateTime origin = new(now.Year - 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        List<MaintenanceLog> logs = [];
        void Add(Printer printer, string? component, DateTime date, double? hours, decimal? cost, int? duration, bool scoped = false)
        {
            logs.Add(new MaintenanceLog
            {
                PrinterId = printer.Id,
                Component = component,
                PerformedAt = date,
                PrinterHoursAtMaintenance = hours,
                Cost = cost,
                DurationMinutes = duration,
                ToolheadId = scoped ? toolhead.Id : null,
                TaskName = "Maintain",
                Notes = new string('n', 2000),
                PartsReplaced = new string('p', 512)
            });
        }

        Add(active, "Hours", origin, 100, null, null);
        Add(active, "Hours", origin.AddDays(1), null, 0, 10);
        Add(active, "Hours", origin.AddDays(2), 120, -1, -5);
        Add(active, "Hours", origin.AddDays(3), 5, null, 0);
        Add(active, "Hours", origin.AddDays(4), 35, null, 20, scoped: true);
        Add(noStatistics, "Hours", origin.AddDays(5), 45, null, null);
        Add(active, "Fallback", origin, null, null, null);
        Add(active, "Fallback", origin.AddHours(12), null, null, null);
        Add(active, "Fallback", origin.AddDays(2), null, null, null);
        Add(active, "Threshold", origin, 0, null, null);
        Add(active, "Threshold", origin.AddDays(1), 0.001, null, null);
        Add(active, "Above threshold", origin, 0, null, null);
        Add(active, "Above threshold", origin.AddDays(1), 0.0011, null, null);
        Add(active, "Midpoint", origin, 0, null, null);
        Add(active, "Midpoint", origin.AddDays(1), 1.25, null, null);
        Add(active, "Reset", origin, 10, null, null);
        Add(active, "Reset", origin.AddDays(1), 0, null, null);
        // Equal dates have no secondary ordering contract. Equal hours make either order equivalent.
        Add(active, "Tied", origin, 5, null, null);
        Add(active, "Tied", origin, 5, null, null);
        Add(active, "Tied", origin.AddDays(1), 15, null, null);
        Add(active, "Single", origin, 1, null, null);
        Add(active, " ", origin, null, null, null);
        Add(active, null, origin, null, null, null);
        Add(active, "", origin, null, null, null);
        Add(active, "Case", origin, 1, null, null);
        Add(active, "case", origin.AddDays(1), 3, null, null);
        Add(active, null, now.AddDays(-1), null, 0.1m, 60);
        Add(active, null, now.AddDays(-1), null, 0.2m, null, scoped: true);
        Add(active, null, now.AddMonths(-25), null, 999m, null);
        Add(active, null, now.AddMonths(-12).AddMilliseconds(-1), null, 700m, null);
        Add(active, null, now.AddMonths(-12), null, 800m, null);
        Add(active, null, now.AddMonths(-12).AddMilliseconds(1), null, 900m, null);
        Add(active, null, new DateTime(now.Year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, 12.34m, null);
        Add(zeroHours, null, origin, null, null, 30);
        Add(negativeDowntime, null, origin, null, null, -30);
        db.MaintenanceLogs.AddRange(logs);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Use the database's own grouping/collation for baseline component membership.
        var groups = await db.MaintenanceLogs.AsNoTracking().GroupBy(l => l.Component!)
            .Where(g => g.Key != null && g.Key != "")
            .Select(g => new { Component = g.Key, Logs = g.OrderBy(l => l.PerformedAt).ToList() })
            .ToListAsync();
        foreach (bool scoped in new[] { false, true })
        {
            capture.Reset();
            // Freeze the SQL parameter so exact boundary parity does not compare moving clocks.
            capture.CostCutoff = now.AddMonths(-12);
            var cost = await repo.GetCostAnalysisAsync(12, scoped);
            capture.CostCutoff = null;
            var qualifying = logs.Where(l => (scoped || l.ToolheadId == null) &&
                l.PerformedAt >= now.AddMonths(-12) && l.Cost > 0);
            cost.Should().Equal(qualifying.GroupBy(l => new { l.PerformedAt.Year, l.PerformedAt.Month })
                .Select(g => new MaintenanceCostEntry($"{g.Key.Year}-{g.Key.Month:D2}", g.Sum(l => l.Cost ?? 0)))
                .OrderBy(e => e.Month));
            capture.Commands.Should().ContainSingle().Which.Should().ContainEquivalentOf("GROUP BY");
            AssertScalarRead(db, capture);

            capture.Reset();
            var uptime = await repo.GetPrinterUptimeAsync(scoped);
            var expected = new[] { active, noStatistics, noHistory, zeroHours, negativeDowntime }.Select(p =>
            {
                var matching = logs.Where(l => l.PrinterId == p.Id && (scoped || l.ToolheadId == null)).ToList();
                int downtime = matching.Sum(l => l.DurationMinutes ?? 0);
                double hours = p.Statistics?.TotalPrintHours ?? 0;
                double percent = hours > 0 && downtime > 0
                    ? Math.Round((1 - downtime / (hours * 60 + downtime)) * 100, 1) : 100;
                return new PrinterUptimeEntry(p.Name, p.Id, percent, matching.Count, downtime);
            }).ToList();
            uptime.Should().BeEquivalentTo(expected);
            uptime.Select(p => p.UptimePercent).Should().BeInDescendingOrder();
            capture.Commands.Should().ContainSingle().Which.Should().ContainEquivalentOf("COUNT(");
            capture.Commands[0].Should().ContainEquivalentOf("SUM(");
            AssertScalarRead(db, capture);

            capture.Reset();
            var lifespan = await repo.GetComponentLifespanAsync(scoped);
            var expectedLifespan = groups.Select(g =>
            {
                var matching = g.Logs.Where(l => scoped || l.ToolheadId == null).ToList();
                var hours = matching.Where(l => l.PrinterHoursAtMaintenance.HasValue)
                    .Select(l => l.PrinterHoursAtMaintenance!.Value).ToList();
                var intervals = hours.Zip(hours.Skip(1), (a, b) => b - a).Where(d => d > 0).ToList();
                double average = intervals.Count == 0 ? 0 : intervals.Average();
                if (average <= 0.001)
                {
                    var days = matching.Zip(matching.Skip(1), (a, b) => (b.PerformedAt - a.PerformedAt).TotalDays)
                        .Where(d => d > 0).ToList();
                    if (days.Count > 0)
                    {
                        average = days.Average() * 8;
                    }
                }

                return new ComponentLifespanEntry(g.Component, Math.Round(average, 1), matching.Count);
            }).Where(g => g.Replacements > 0).ToList();
            lifespan.Should().BeEquivalentTo(expectedLifespan);
            lifespan.Select(l => l.Replacements).Should().BeInDescendingOrder();
            capture.Commands.Should().ContainSingle();
            AssertScalarRead(db, capture);
            output.WriteLine($"{db.Database.ProviderName}, includeToolheadScope={scoped}: {capture.Commands[0]}");
        }
    }

    private static Printer NewPrinter(string name, PrinterModel model, Manufacturer manufacturer) => new()
    {
        Name = name,
        ServerUrl = $"http://{Guid.NewGuid():N}.local",
        BackendPort = 7125,
        Model = model,
        Manufacturer = manufacturer,
        Backend = (int)PrinterBackend.Moonraker
    };

    private static void AssertScalarRead(AppDbContext db, AnalyticsCapture capture)
    {
        db.ChangeTracker.Entries().Should().BeEmpty();
        capture.Entities.Should().Be(0);
        foreach (string command in capture.Commands)
        {
            command.Should().NotContain("Notes").And.NotContain("PartsReplaced")
                .And.NotContain("TaskName").And.NotContain("ServerUrl");
        }
    }

    private sealed class AnalyticsCapture : DbCommandInterceptor, IMaterializationInterceptor
    {
        public List<string> Commands { get; } = [];
        public int Entities { get; private set; }
        public DateTime? CostCutoff { get; set; }

        public void Reset()
        {
            Commands.Clear();
            Entities = 0;
        }

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            Entities++;
            return entity;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (CostCutoff.HasValue)
            {
                var cutoff = command.Parameters.Cast<DbParameter>()
                    .Single(p => p.ParameterName.Contains("startDate", StringComparison.Ordinal));
                ((DateTime)cutoff.Value!).Should().BeCloseTo(DateTime.UtcNow.AddMonths(-12), TimeSpan.FromSeconds(10));
                cutoff.Value = CostCutoff.Value;
            }

            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
