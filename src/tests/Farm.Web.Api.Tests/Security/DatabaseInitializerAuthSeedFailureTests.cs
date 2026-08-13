using System.Data.Common;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Web.Api.Services.Startup;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Regression coverage for issue #1491: <c>DatabaseInitializer.SeedAuthenticationDataAsync</c> had
/// two silent-skip paths that let the whole authentication seed (actions, resources, roles, role
/// permissions) disappear without a trace:
///
/// <list type="number">
/// <item>a blanket <c>catch (Exception)</c> around the <c>UserActions</c> probe query that
/// discarded any exception and returned with no logging at all; and</item>
/// <item>a unique-constraint retry loop that, once exhausted, fell through and returned normally
/// with only a <c>LogDebug</c> per attempt (below production log level).</item>
/// </list>
///
/// Both meant <c>InitializeAsync</c>'s retry/failure handling never saw a problem, so startup
/// would report a successful seed even though no roles or permissions existed. These tests assert
/// the fixed behavior: a probe failure that is not a known missing-table condition propagates and
/// is logged at warning/error, and an exhausted unique-constraint retry loop fails loudly instead
/// of returning silently.
/// </summary>
public sealed class DatabaseInitializerAuthSeedFailureTests
{
    [Fact]
    public async Task SeedAllAsync_WhenAuthProbeFailsForUnexpectedReason_PropagatesAndLogsError()
    {
        // Arrange: the schema exists (so this is NOT the tolerated "no such table" case), but the
        // probe query against UserActions fails for some other reason (e.g. a transient
        // connection problem, a permissions error, a provider quirk).
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        var probeInterceptor = new FailingCommandInterceptor("UserActions", new TimeoutException("Simulated transient connection failure"));
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(probeInterceptor)
            .Options;

        await using (AppDbContext schemaContext = new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options))
        {
            _ = await schemaContext.Database.EnsureCreatedAsync();
        }

        var recordingLogger = new RecordingLogger<DatabaseInitializer>();
        var dataSeedService = new Mock<IDataSeedService>(MockBehavior.Loose);
        await using AppDbContext context = new(options);
        DatabaseInitializer initializer = new(context, recordingLogger, dataSeedService.Object);

        // Act
        Func<Task> act = () => initializer.SeedAllAsync();

        // Assert: the probe failure is not swallowed — it propagates out of SeedAllAsync so the
        // caller's retry/failure handling actually sees it, instead of the entire authentication
        // seed silently disappearing.
        _ = await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("Simulated transient connection failure");

        // Assert: the underlying reason was logged at warning or error level, naming the failure —
        // not discarded with zero log output as the pre-fix blanket catch did.
        recordingLogger.Entries.Should().Contain(
            entry => (entry.Level == LogLevel.Warning || entry.Level == LogLevel.Error)
                && entry.Exception != null && entry.Exception.GetType() == typeof(TimeoutException),
            "a probe failure that is not a known missing-table condition must be logged at " +
            "warning/error with the underlying exception, not silently discarded");

        // Assert: nothing from the authentication seed was persisted — this really is a hard
        // failure, not a partial silent success.
        (await context.Roles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SeedAllAsync_WhenUniqueConstraintRetryLoopExhausts_ThrowsInsteadOfReturningSilently()
    {
        // Arrange: force every SaveChangesAsync call during the authentication seed to raise a
        // unique-constraint violation, so all three retry attempts are exhausted.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        await using (AppDbContext schemaContext = new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options))
        {
            _ = await schemaContext.Database.EnsureCreatedAsync();
        }

        var alwaysRacingInterceptor = new AlwaysUniqueConstraintViolationInterceptor();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(alwaysRacingInterceptor)
            .Options;

        var recordingLogger = new RecordingLogger<DatabaseInitializer>();
        var dataSeedService = new Mock<IDataSeedService>(MockBehavior.Loose);
        await using AppDbContext context = new(options);
        DatabaseInitializer initializer = new(context, recordingLogger, dataSeedService.Object);

        // Act
        Func<Task> act = () => initializer.SeedAllAsync();

        // Assert: exhausting the retry loop must fail startup, not return normally as if seeding
        // succeeded.
        _ = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unique constraint*");

        // Assert: the failure was logged at error level (not LogDebug, which sits below
        // production log level and was the pre-fix behavior).
        recordingLogger.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Error,
            "exhausting the unique-constraint retry loop must be logged at error level, not " +
            "only LogDebug");
    }

    /// <summary>
    /// Raises the given exception the first time a command whose text contains
    /// <paramref name="tableNameFragment"/> executes a reader, simulating an unexpected probe
    /// failure unrelated to a missing table.
    /// </summary>
    private sealed class FailingCommandInterceptor(string tableNameFragment, Exception exceptionToThrow) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(tableNameFragment, StringComparison.Ordinal))
            {
                throw exceptionToThrow;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Forces every attempt of <c>SeedAuthenticationDataAsync</c>'s unique-constraint retry loop
    /// to fail by adding a duplicate <see cref="Farm.Infrastructure.Domain.UserAction"/> row
    /// (same unique <c>Name</c>, different <c>Id</c>) into the same SaveChanges batch right
    /// before it executes, guaranteeing a SQLite unique-constraint violation on every single
    /// attempt regardless of what any previous attempt did.
    /// </summary>
    private sealed class AlwaysUniqueConstraintViolationInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is AppDbContext context)
            {
                var addedAction = context.ChangeTracker.Entries<Farm.Infrastructure.Domain.UserAction>()
                    .FirstOrDefault(entry => entry.State == EntityState.Added);
                if (addedAction is not null)
                {
                    _ = context.UserActions.Add(new Farm.Infrastructure.Domain.UserAction
                    {
                        Id = Guid.NewGuid(),
                        Name = addedAction.Entity.Name,
                        DisplayName = addedAction.Entity.DisplayName,
                        Description = addedAction.Entity.Description,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    });
                }
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed record RecordedLogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<RecordedLogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new RecordedLogEntry(logLevel, exception, formatter(state, exception)));
        }
    }
}
