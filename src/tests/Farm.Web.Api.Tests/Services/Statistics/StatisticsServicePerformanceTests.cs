using System.Data.Common;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Statistics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Statistics;

public class StatisticsServicePerformanceTests
{
    [Fact]
    public async Task GetSummaryAsync_UsesTwoDatabaseCommandsForAggregateMetrics()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        var interceptor = new CommandCountingInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "performance-proxy.gcode",
            Status = PrintJobStatus.Completed,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ActualCost = 2.5m,
            ActualFilamentUsage = 12.5d,
            ActualPrintTime = TimeSpan.FromMinutes(30),
        });
        await db.SaveChangesAsync();
        interceptor.Reset();

        StatisticsSummaryDto result = await new StatisticsService(db).GetSummaryAsync(null);

        Assert.Equal(1, result.TotalJobs);
        Assert.Equal(2.5m, result.TotalCost);
        Assert.Equal(12.5d, result.TotalFilamentGrams);
        Assert.Equal(0.5d, result.TotalPrintHours);
        Assert.Equal(2, interceptor.NonSchemaCommandCount);
    }

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        public int NonSchemaCommandCount { get; private set; }

        public void Reset()
        {
            NonSchemaCommandCount = 0;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            if (!IsSchemaCommand(command))
            {
                NonSchemaCommandCount++;
            }

            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!IsSchemaCommand(command))
            {
                NonSchemaCommandCount++;
            }

            return ValueTask.FromResult(result);
        }

        private static bool IsSchemaCommand(DbCommand command)
        {
            return command.CommandText.Contains("sqlite_master", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase);
        }
    }
}
