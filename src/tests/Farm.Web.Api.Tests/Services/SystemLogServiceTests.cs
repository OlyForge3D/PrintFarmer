using System;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.SystemLogs;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class SystemLogServiceTests
{
    private static AppDbContext CreateDbContext()
    {
        return TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
    }

    [Fact]
    public async Task QueryLogsAsync_FiltersByCorrelationIdAndLevelAndMetadata()
    {
        await using var db = CreateDbContext();
        db.SystemLogs.AddRange(
            new SystemLog { CorrelationId = "c1", Level = "Info", Timestamp = DateTime.UtcNow.AddMinutes(-10), Metadata = "foo" },
            new SystemLog { CorrelationId = "c2", Level = "Error", Timestamp = DateTime.UtcNow.AddMinutes(-5), Metadata = "bar" },
            new SystemLog { CorrelationId = "c1", Level = "Error", Timestamp = DateTime.UtcNow, Metadata = "baz" }
        );
        await db.SaveChangesAsync();

        var repo = new Farm.Infrastructure.Repositories.SystemLogs.EfSystemLogRepository(db);
        var svc = new SystemLogService(repo);
        var results = await svc.QueryLogsAsync("c1", "Error", null, null, null, default);

        Assert.Single(results);
        Assert.Equal("c1", results[0]!.CorrelationId);
        Assert.Equal("Error", results[0]!.Level);
    }

    [Fact]
    public async Task QueryAllLogsAsync_ReturnsAllMatching()
    {
        await using var db = CreateDbContext();
        db.SystemLogs.AddRange(
            new SystemLog { CorrelationId = "c1", Level = "Info", Timestamp = DateTime.UtcNow.AddMinutes(-10), Metadata = "foo" },
            new SystemLog { CorrelationId = "c2", Level = "Error", Timestamp = DateTime.UtcNow.AddMinutes(-5), Metadata = "bar" },
            new SystemLog { CorrelationId = "c1", Level = "Error", Timestamp = DateTime.UtcNow, Metadata = "baz" }
        );
        await db.SaveChangesAsync();

        var repo = new Farm.Infrastructure.Repositories.SystemLogs.EfSystemLogRepository(db);
        var svc = new SystemLogService(repo);
        var results = await svc.QueryAllLogsAsync(null, null, null, null, "ba", default);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Metadata!.Contains("bar"));
        Assert.Contains(results, r => r.Metadata!.Contains("baz"));
    }
}
