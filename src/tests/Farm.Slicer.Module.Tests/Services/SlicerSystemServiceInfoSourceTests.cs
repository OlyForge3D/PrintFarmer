using System.Data.Common;
using Farm.Infrastructure.Dtos;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services.SystemInfo;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

public sealed class SlicerSystemServiceInfoSourceTests
{
    [Theory]
    [InlineData("Online", 0, SystemServiceHealth.Healthy)]
    [InlineData("Busy", 0, SystemServiceHealth.Healthy)]
    [InlineData("Draining", 0, SystemServiceHealth.Healthy)]
    [InlineData("Online", 121, SystemServiceHealth.Degraded)]
    [InlineData("Online", -60, SystemServiceHealth.Degraded)]
    [InlineData("Offline", 0, SystemServiceHealth.Degraded)]
    [InlineData("Unknown", 0, SystemServiceHealth.Degraded)]
    [InlineData(null, 0, SystemServiceHealth.Degraded)]
    [InlineData("Error", 0, SystemServiceHealth.Critical)]
    public async Task ReadAsync_RegistryStatusAndHeartbeat_ReportsHealth(string? status, int ageSeconds, SystemServiceHealth expected)
    {
        await using SlicerDbContext db = CreateContext();
        db.SlicerServices.Add(new SlicerService
        {
            Name = "OrcaSlicer",
            Version = "2.4.2",
            Status = status,
            LastSeen = DateTime.UtcNow.AddSeconds(-ageSeconds),
            CapabilitiesJson = "{\"applicationBuild\":\"1.2.3+sha.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}",
        });
        await db.SaveChangesAsync();
        SlicerSystemServiceInfoSource source = new(db, NullLogger<SlicerSystemServiceInfoSource>.Instance);

        SystemServiceInfoDto row = Assert.Single(await source.ReadAsync(CancellationToken.None));

        Assert.Equal("Slicer worker (OrcaSlicer)", row.Name);
        Assert.Equal("1.2.3", row.Version);
        Assert.Equal("2.4.2", row.EngineVersion);
        Assert.Equal(expected, row.Health);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("{\"applicationBuild\":42}")]
    [InlineData("{\"applicationBuild\":\"latest\"}")]
    public async Task ReadAsync_LegacyCapabilities_KeepsEngineSeparateFromUnknownApplication(string? capabilities)
    {
        await using SlicerDbContext db = CreateContext();
        db.SlicerServices.Add(new SlicerService { Version = "2.4.2", Status = "Online", CapabilitiesJson = capabilities });
        await db.SaveChangesAsync();
        SlicerSystemServiceInfoSource source = new(db, NullLogger<SlicerSystemServiceInfoSource>.Instance);

        SystemServiceInfoDto row = Assert.Single(await source.ReadAsync(CancellationToken.None));

        Assert.Equal("Unknown", row.Version);
        Assert.Equal("2.4.2", row.EngineVersion);
    }

    [Fact]
    public async Task ReadAsync_EmptyRegistry_ReturnsNoOptionalRows()
    {
        await using SlicerDbContext db = CreateContext();
        SlicerSystemServiceInfoSource source = new(db, NullLogger<SlicerSystemServiceInfoSource>.Instance);
        Assert.Empty(await source.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_DisabledRegistration_IsRetainedAsDegraded()
    {
        await using SlicerDbContext db = CreateContext();
        db.SlicerServices.Add(new SlicerService { Status = "Disabled" });
        await db.SaveChangesAsync();
        SlicerSystemServiceInfoSource source = new(db, NullLogger<SlicerSystemServiceInfoSource>.Instance);

        SystemServiceInfoDto row = Assert.Single(await source.ReadAsync(CancellationToken.None));

        Assert.Equal(SystemServiceHealth.Degraded, row.Health);
        Assert.Contains("Slicer worker", row.Name);
    }

    [Fact]
    public async Task ReadAsync_MissingRegistry_ReturnsNoOptionalRows()
    {
        SlicerSystemServiceInfoSource source = new(null, NullLogger<SlicerSystemServiceInfoSource>.Instance);
        Assert.Empty(await source.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_RegistryFailure_LogsWarningAndReportsUnavailable()
    {
        SlicerDbContext db = CreateContext();
        await db.DisposeAsync();
        Mock<ILogger<SlicerSystemServiceInfoSource>> logger = new();
        SlicerSystemServiceInfoSource source = new(db, logger.Object);

        SystemServiceInfoDto row = Assert.Single(await source.ReadAsync(CancellationToken.None));

        Assert.Equal(SystemServiceHealth.Degraded, row.Health);
        Assert.Contains("registry unavailable", row.Name);
        VerifyWarning(logger);
    }

    [Fact]
    public async Task ReadAsync_RegistryQueryFailure_LogsWarningAndReportsUnavailable()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        ThrowOnRegistryReadInterceptor interceptor = new(new InvalidOperationException("Registry query failed"));
        await using SlicerDbContext db = await CreateSqliteContextAsync(connection, interceptor);
        db.SlicerServices.Add(new SlicerService { Status = "Online" });
        await db.SaveChangesAsync();
        Mock<ILogger<SlicerSystemServiceInfoSource>> logger = new();
        SlicerSystemServiceInfoSource source = new(db, logger.Object);

        SystemServiceInfoDto row = Assert.Single(await source.ReadAsync(CancellationToken.None));

        Assert.Equal(SystemServiceHealth.Degraded, row.Health);
        Assert.Contains("registry unavailable", row.Name);
        VerifyWarning(logger);
    }

    [Fact]
    public async Task ReadAsync_RegistryQueryCancellation_PropagatesCancellation()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        ThrowOnRegistryReadInterceptor interceptor = new(new OperationCanceledException("Registry query canceled"));
        await using SlicerDbContext db = await CreateSqliteContextAsync(connection, interceptor);
        db.SlicerServices.Add(new SlicerService { Status = "Online" });
        await db.SaveChangesAsync();
        SlicerSystemServiceInfoSource source = new(db, NullLogger<SlicerSystemServiceInfoSource>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task ReadAsync_NonUtcLastSeen_IsNormalizedForHealth(DateTimeKind kind)
    {
        await using SlicerDbContext db = CreateContext();
        DateTime lastSeenUtc = DateTime.UtcNow.AddSeconds(-30);
        DateTime lastSeen = kind == DateTimeKind.Local
            ? lastSeenUtc.ToLocalTime()
            : DateTime.SpecifyKind(lastSeenUtc, DateTimeKind.Unspecified);
        db.SlicerServices.Add(new SlicerService { Status = "Online", LastSeen = lastSeen });
        await db.SaveChangesAsync();
        SlicerSystemServiceInfoSource source = new(db, NullLogger<SlicerSystemServiceInfoSource>.Instance);

        SystemServiceInfoDto row = Assert.Single(await source.ReadAsync(CancellationToken.None));

        Assert.Equal(SystemServiceHealth.Healthy, row.Health);
    }

    [Fact]
    public async Task ReadAsync_MalformedCapabilities_LogsWarningWithoutDiscardingEngineHealth()
    {
        await using SlicerDbContext db = CreateContext();
        db.SlicerServices.Add(new SlicerService { Status = "Online", Version = "2.4.2", CapabilitiesJson = "{not-json" });
        await db.SaveChangesAsync();
        Mock<ILogger<SlicerSystemServiceInfoSource>> logger = new();
        SlicerSystemServiceInfoSource source = new(db, logger.Object);

        SystemServiceInfoDto row = Assert.Single(await source.ReadAsync(CancellationToken.None));

        Assert.Equal("Unknown", row.Version);
        Assert.Equal("2.4.2", row.EngineVersion);
        Assert.Equal(SystemServiceHealth.Healthy, row.Health);
        VerifyWarning(logger);
    }

    [Fact]
    public async Task ReadAsync_Cancelled_DoesNotReturnSuccessShapedRows()
    {
        SlicerSystemServiceInfoSource source = new(null, NullLogger<SlicerSystemServiceInfoSource>.Instance);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(cancellation.Token));
    }

    private static SlicerDbContext CreateContext() => new(new DbContextOptionsBuilder<SlicerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<SlicerDbContext> CreateSqliteContextAsync(
        SqliteConnection connection,
        DbCommandInterceptor interceptor)
    {
        SlicerDbContext db = new(new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static void VerifyWarning(Mock<ILogger<SlicerSystemServiceInfoSource>> logger) => logger.Verify(value => value.Log(
        LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(),
        It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);

    private sealed class ThrowOnRegistryReadInterceptor(Exception failure) : DbCommandInterceptor
    {
        public Exception? Failure { get; } = failure;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Failure is not null
                && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("SlicerServices", StringComparison.OrdinalIgnoreCase))
            {
                throw Failure;
            }

            return ValueTask.FromResult(result);
        }
    }
}
