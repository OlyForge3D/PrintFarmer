using Farm.Infrastructure.Data;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Services.Startup;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests.Logging;

/// <summary>
/// Regression coverage for issue #1567's pre-schema database logging.
/// </summary>
public sealed class SystemLogLoggerProviderStartupTests
{
    [Fact]
    public async Task LogWarning_BeforeDatabaseSchemaReady_DoesNotResolveDatabaseServices()
    {
        bool databaseSchemaReady = false;
        var schemaCheckObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupStatus = new Mock<IStartupStatus>();
        _ = startupStatus
            .SetupGet(status => status.IsDatabaseSchemaReady)
            .Returns(() =>
            {
                _ = schemaCheckObserved.TrySetResult();
                return Volatile.Read(ref databaseSchemaReady);
            });
        _ = startupStatus.SetupGet(status => status.IsFailed).Returns(false);
        int settingsResolutionCount = 0;
        int dbResolutionCount = 0;
        var dbResolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settingsService = new Mock<ISettingsService>();
        _ = settingsService
            .Setup(service => service.Get<SystemLogSettings>())
            .Returns(new SystemLogSettings());

        var services = new ServiceCollection();
        _ = services.AddHttpContextAccessor();
        _ = services.AddScoped<ISettingsService>(_ =>
        {
            settingsResolutionCount++;
            return settingsService.Object;
        });
        DbContextOptions<AppDbContext> dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"system-log-startup-{Guid.NewGuid()}")
            .Options;
        _ = services.AddScoped(_serviceProvider =>
        {
            _ = Interlocked.Increment(ref dbResolutionCount);
            _ = dbResolved.TrySetResult();
            return new AppDbContext(dbOptions);
        });

        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var provider = new SystemLogLoggerProvider(
            serviceProvider,
            startupStatus.Object,
            LogLevel.Warning);
        ILogger logger = provider.CreateLogger("Startup");

        await schemaCheckObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));

        for (int index = 0; index < 50; index++)
        {
            logger.LogWarning("Warning {Index} emitted before schema initialization", index);
        }

        settingsResolutionCount.Should().Be(
            0,
            "database-backed settings must not be resolved before migrations complete and the host starts");
        Volatile.Read(ref dbResolutionCount).Should().Be(
            0,
            "queued log writes must not resolve AppDbContext before migrations complete");

        Volatile.Write(ref databaseSchemaReady, true);
        logger.LogWarning("Warning emitted after schema initialization");

        settingsResolutionCount.Should().Be(1);
        Task completed = await Task.WhenAny(dbResolved.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        completed.Should().Be(
            dbResolved.Task,
            "queued database log writes should resume once the schema-ready signal is set");

        await using var assertionContext = new AppDbContext(dbOptions);
        List<string> persistedPreSchemaLogs = [];
        DateTime persistenceDeadline = DateTime.UtcNow.AddSeconds(3);
        while (persistedPreSchemaLogs.Count < 50 && DateTime.UtcNow < persistenceDeadline)
        {
            persistedPreSchemaLogs = await assertionContext.SystemLogs
                .Where(log => log.Message.StartsWith("Warning ") && log.Message.Contains("before schema initialization"))
                .Select(log => log.Message)
                .ToListAsync();
            if (persistedPreSchemaLogs.Count < 50)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }
        }

        string[] expectedPreSchemaLogs = Enumerable.Range(0, 50)
            .Select(index => $"Warning {index} emitted before schema initialization")
            .ToArray();
        persistedPreSchemaLogs.Should().BeEquivalentTo(
            expectedPreSchemaLogs,
            "all logs queued before schema readiness should be persisted after the gate opens");
    }
}
