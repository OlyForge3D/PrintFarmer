using System.Reflection;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.Notifications;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Notifications;

public sealed class PreferenceConcurrencyRetryTests
{
    [Fact]
    public void Classify_DirectExactDbUpdateConcurrency_IsTransient()
    {
        PreferenceConcurrencyRetry.Classify(new DbUpdateConcurrencyException("conflict"))
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.TransientProviderConflict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Classify_ExactSqliteBusy_DirectOrImmediateDbUpdateWrapper_IsTransient(bool wrapped)
    {
        var provider = new SqliteException("database is locked", 5);
        Exception exception = wrapped ? new DbUpdateException("save", provider) : provider;

        PreferenceConcurrencyRetry.Classify(exception)
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.TransientProviderConflict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Classify_ExactPostgresSerialization_DirectOrImmediateDbUpdateWrapper_IsTransient(bool wrapped)
    {
        var provider = new PostgresException("serialization", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure);
        Exception exception = wrapped ? new DbUpdateException("save", provider) : provider;

        PreferenceConcurrencyRetry.Classify(exception)
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.TransientProviderConflict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Classify_ExactSqlServerDeadlock_DirectOrImmediateDbUpdateWrapper_IsTransient(bool wrapped)
    {
        SqlException provider = CreateSqlException(1205, "deadlock victim");
        Exception exception = wrapped ? new DbUpdateException("save", provider) : provider;

        PreferenceConcurrencyRetry.Classify(exception)
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.TransientProviderConflict);
    }

    [Theory]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: NotificationPreferences.UserId'.", 2067, true)]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: Other.UserId'.", 2067, false)]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: NotificationPreferences.UserId, NotificationPreferences.Id'.", 2067, false)]
    [InlineData("FOREIGN KEY constraint failed", 787, false)]
    [InlineData("CHECK constraint failed", 275, false)]
    public void Classify_SqliteConstraint_IsExact(string message, int extendedCode, bool expectedRetry)
    {
        var exception = new SqliteException(message, 19, extendedCode);

        PreferenceConcurrencyRetry.ClassifierDecision decision = PreferenceConcurrencyRetry.Classify(exception);

        decision.Should().Be(expectedRetry
            ? PreferenceConcurrencyRetry.ClassifierDecision.UserIdUniqueConflict
            : PreferenceConcurrencyRetry.ClassifierDecision.Rethrow);
    }

    [Fact]
    public void Classify_PostgresUnique_RequiresExactConstraintName()
    {
        var expected = new PostgresException(
            "duplicate",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            constraintName: "IX_NotificationPreferences_UserId");
        var unrelated = new PostgresException(
            "duplicate",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            constraintName: "IX_NotificationPreferences_UserId_And_Other");
        var wrongCase = new PostgresException(
            "duplicate",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            constraintName: "ix_notificationpreferences_userid");

        PreferenceConcurrencyRetry.Classify(expected)
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.UserIdUniqueConflict);
        PreferenceConcurrencyRetry.Classify(unrelated)
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.Rethrow);
        PreferenceConcurrencyRetry.Classify(wrongCase)
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.Rethrow);
    }

    [Fact]
    public void Classify_SqlServerUnique_RequiresDelimitedExactIndexName()
    {
        SqlException expected = CreateSqlException(
            2627,
            "Violation of UNIQUE KEY constraint 'IX_NotificationPreferences_UserId'.");
        SqlException unrelated = CreateSqlException(
            2627,
            "Violation of UNIQUE KEY constraint 'IX_NotificationPreferences_UserId_And_Other'.");

        PreferenceConcurrencyRetry.Classify(expected)
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.UserIdUniqueConflict);
        PreferenceConcurrencyRetry.Classify(unrelated)
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.Rethrow);
    }

    [Fact]
    public void Classify_ArbitraryAndDeepWrappers_AreRejected()
    {
        var provider = new SqliteException("database is locked", 5);
        Exception arbitrary = new ArbitraryWrapperException("outer", provider);
        Exception deep = new DbUpdateException(
            "save",
            new ArbitraryWrapperException("middle", provider));
        Exception wrappedConcurrency = new ArbitraryWrapperException(
            "outer",
            new DbUpdateConcurrencyException("conflict"));

        PreferenceConcurrencyRetry.Classify(arbitrary)
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.Rethrow);
        PreferenceConcurrencyRetry.Classify(deep)
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.Rethrow);
        PreferenceConcurrencyRetry.Classify(wrappedConcurrency)
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.Rethrow);
    }

    [Fact]
    public void Classify_ShortNameAndDbExceptionLookAlikes_AreRejected()
    {
        PreferenceConcurrencyRetry.Classify(new SqliteExceptionLookAlike("database is locked"))
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.Rethrow);
        PreferenceConcurrencyRetry.Classify(new DbExceptionLookAlike("database is locked"))
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.Rethrow);
    }

    [Fact]
    public async Task ExecuteAsync_Conflict_RetriesExactlyOnceWithFreshContext()
    {
        using var factory = new CountingFactory();
        int attempts = 0;
        int classifierCalls = 0;
        var contextIds = new List<DbContextId>();

        int result = await PreferenceConcurrencyRetry.ExecuteAsync(
            factory,
            fallbackContext: null,
            operation: (context, _) =>
            {
                contextIds.Add(context.ContextId);
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new DbUpdateConcurrencyException("conflict");
                }

                return Task.FromResult(42);
            },
            logger: NullLogger.Instance,
            cancellationToken: CancellationToken.None,
            classifier: exception =>
            {
                classifierCalls++;
                return PreferenceConcurrencyRetry.Classify(exception);
            });

        result.Should().Be(42);
        attempts.Should().Be(2);
        classifierCalls.Should().Be(1);
        factory.CreatedContexts.Should().Be(2);
        contextIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedFailure_PreservesOriginalAndDoesNotRetry()
    {
        using var factory = new CountingFactory();
        var original = new DbUpdateException(
            "foreign key violation",
            new SqliteException("FOREIGN KEY constraint failed", 19, 787));
        int attempts = 0;

        Func<Task> act = () => PreferenceConcurrencyRetry.ExecuteAsync<int>(
            factory,
            fallbackContext: null,
            operation: (_, _) =>
            {
                attempts++;
                throw original;
            },
            logger: NullLogger.Instance,
            cancellationToken: CancellationToken.None);

        (await act.Should().ThrowAsync<DbUpdateException>()).Which.Should().BeSameAs(original);
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_RepeatedConflict_StopsAtExactBoundAndPreservesLast()
    {
        using var factory = new CountingFactory();
        int attempts = 0;
        DbUpdateConcurrencyException? last = null;

        Func<Task> act = () => PreferenceConcurrencyRetry.ExecuteAsync<int>(
            factory,
            fallbackContext: null,
            operation: (_, _) =>
            {
                last = new DbUpdateConcurrencyException($"conflict-{++attempts}");
                throw last;
            },
            logger: NullLogger.Instance,
            cancellationToken: CancellationToken.None);

        (await act.Should().ThrowAsync<DbUpdateConcurrencyException>()).Which.Should().BeSameAs(last);
        attempts.Should().Be(PreferenceConcurrencyRetry.MaxAttempts);
        factory.CreatedContexts.Should().Be(PreferenceConcurrencyRetry.MaxAttempts);
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_DoesNotClassifyOrRetry()
    {
        using var factory = new CountingFactory();
        int classifierCalls = 0;

        Func<Task> act = () => PreferenceConcurrencyRetry.ExecuteAsync<int>(
            factory,
            fallbackContext: null,
            operation: (_, _) => throw new OperationCanceledException("cancelled"),
            logger: NullLogger.Instance,
            cancellationToken: CancellationToken.None,
            classifier: exception =>
            {
                classifierCalls++;
                return PreferenceConcurrencyRetry.Classify(exception);
            });

        await act.Should().ThrowAsync<OperationCanceledException>();
        classifierCalls.Should().Be(0);
        factory.CreatedContexts.Should().Be(1);
    }

    private static SqlException CreateSqlException(int number, string message)
    {
        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        var collection = (SqlErrorCollection)Activator.CreateInstance(
            typeof(SqlErrorCollection),
            instanceFlags,
            binder: null,
            args: null,
            culture: null)!;
        ConstructorInfo errorConstructor = typeof(SqlError)
            .GetConstructors(instanceFlags)
            .OrderByDescending(constructor => constructor.GetParameters().Length)
            .First();
        object?[] errorArguments = BuildArguments(errorConstructor.GetParameters(), number, message, collection);
        var error = (SqlError)errorConstructor.Invoke(errorArguments);
        _ = typeof(SqlErrorCollection)
            .GetMethod("Add", instanceFlags)!
            .Invoke(collection, [error]);

        MethodInfo factory = typeof(SqlException)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name == "CreateException")
            .First(method => method.GetParameters().Length >= 2
                && method.GetParameters()[0].ParameterType == typeof(SqlErrorCollection));
        return (SqlException)factory.Invoke(
            null,
            BuildArguments(factory.GetParameters(), number, message, collection))!;
    }

    private static object?[] BuildArguments(
        ParameterInfo[] parameters,
        int number,
        string message,
        SqlErrorCollection collection)
    {
        return parameters.Select(parameter =>
        {
            if (parameter.ParameterType == typeof(SqlErrorCollection))
            {
                return (object)collection;
            }

            if (parameter.ParameterType == typeof(int))
            {
                return parameter.Name?.Contains("number", StringComparison.OrdinalIgnoreCase) == true
                    ? number
                    : 0;
            }

            if (parameter.ParameterType == typeof(byte))
            {
                return (byte)0;
            }

            if (parameter.ParameterType == typeof(uint))
            {
                return 0U;
            }

            if (parameter.ParameterType == typeof(Guid))
            {
                return Guid.Empty;
            }

            if (parameter.ParameterType == typeof(string))
            {
                return parameter.Name?.Contains("message", StringComparison.OrdinalIgnoreCase) == true
                    ? message
                    : "test";
            }

            return null;
        }).ToArray();
    }

    private sealed class ArbitraryWrapperException(string message, Exception inner)
        : Exception(message, inner);

    private sealed class SqliteExceptionLookAlike(string message) : Exception(message);

    private sealed class DbExceptionLookAlike(string message) : System.Data.Common.DbException(message);

    private sealed class CountingFactory : IDbContextFactory<AppDbContext>, IDisposable
    {
        private readonly SqliteConnection _keepAlive;
        private readonly DbContextOptions<AppDbContext> _options;

        public CountingFactory()
        {
            string connectionString = $"Data Source=file:retry-{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(connectionString);
            _keepAlive.Open();
            _options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;
            using var context = new AppDbContext(_options);
            _ = context.Database.EnsureCreated();
        }

        public int CreatedContexts { get; private set; }

        public AppDbContext CreateDbContext()
        {
            CreatedContexts++;
            return new AppDbContext(_options);
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());

        public void Dispose() => _keepAlive.Dispose();
    }
}
