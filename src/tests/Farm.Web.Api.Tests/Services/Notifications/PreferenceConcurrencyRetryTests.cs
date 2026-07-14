using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.Notifications;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Notifications;

/// <summary>
/// Hicks #3 dedicated retry classifier + attempt + fresh-context coverage.
///
/// PreferenceConcurrencyRetry is the single production surface that wraps
/// every preference write. These tests exercise it in isolation:
///   * classifier accepts DbUpdateConcurrencyException, provider-specific
///     transient codes (SQLite BUSY, extended UNIQUE), and rejects unrelated
///     constraint / FK / CHECK / arbitrary-unique failures;
///   * transient exceptions retry to success on a subsequent attempt;
///   * expected UserId unique conflicts retry;
///   * unrelated unique / FK / CHECK constraints DO NOT retry;
///   * the retry bound is enforced (MaxAttempts);
///   * cancellation consumes no retry budget and propagates without
///     classifier interception (Hicks #1 parity);
///   * every attempt receives a FRESH DbContext instance from the factory —
///     tracker state from a losing attempt never leaks into the retry.
///
/// The tests deliberately do NOT depend on a specific database provider
/// assembly beyond Microsoft.Data.Sqlite (already referenced by the test
/// project). Provider-specific exception shapes are exercised through
/// hand-crafted test-double exception types whose property surface mirrors
/// the real provider surface the classifier reads by reflection.
/// </summary>
public sealed class PreferenceConcurrencyRetryTests
{
    /// <summary>
    /// Classifier accepts <see cref="DbUpdateConcurrencyException"/> as
    /// transient. This is EF's native optimistic-concurrency signal — under
    /// SERIALIZABLE isolation on PostgreSQL / SQL Server it is the exact
    /// exception the losing writer sees.
    /// </summary>
    [Fact]
    public void Classify_DbUpdateConcurrencyException_IsTransientProviderConflict()
    {
        var ex = new DbUpdateConcurrencyException("conflict");

        PreferenceConcurrencyRetry.ClassifierDecision decision = PreferenceConcurrencyRetry.Classify(ex);

        decision.Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.TransientProviderConflict);
    }

    /// <summary>
    /// Classifier walks the InnerException chain — a raw provider exception
    /// nested inside a DbUpdateException MUST still be recognised.
    /// </summary>
    [Fact]
    public void Classify_SqliteBusy_WrappedInDbUpdateException_IsTransient()
    {
        // SQLite error code 5 = SQLITE_BUSY (transient).
        var sqliteEx = new SqliteException("database is locked", 5);
        var wrapped = new DbUpdateException("outer", sqliteEx);

        PreferenceConcurrencyRetry.ClassifierDecision decision = PreferenceConcurrencyRetry.Classify(wrapped);

        decision.Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.TransientProviderConflict);
    }

    /// <summary>
    /// Classifier detects SQLite UserId-unique conflict via extended code
    /// 2067 (SQLITE_CONSTRAINT_UNIQUE) AND a message that references the
    /// UserId column — arbitrary UNIQUE conflicts on unrelated columns fall
    /// through to Rethrow.
    /// </summary>
    [Fact]
    public void Classify_SqliteUniqueOnUserId_IsUserIdUniqueConflict()
    {
        // SqliteException surfaces both primary (19) and extended (2067)
        // codes. We construct one with the extended code so the classifier's
        // TryGetSqliteErrorCode reflection reads 2067.
        var sqliteEx = new SqliteException(
            "SQLite Error 19: 'UNIQUE constraint failed: NotificationPreferences.UserId'.",
            19,
            2067);

        PreferenceConcurrencyRetry.ClassifierDecision decision = PreferenceConcurrencyRetry.Classify(sqliteEx);

        decision.Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.UserIdUniqueConflict);
    }

    /// <summary>
    /// SQLite UNIQUE conflict on a DIFFERENT column (arbitrary schema
    /// artefact) MUST fall through to Rethrow so a genuine schema fault is
    /// surfaced rather than masked by the retry loop.
    /// </summary>
    [Fact]
    public void Classify_SqliteUniqueOnUnrelatedColumn_Rethrow()
    {
        var sqliteEx = new SqliteException(
            "SQLite Error 19: 'UNIQUE constraint failed: SomeOther.SomeColumn'.",
            19,
            2067);

        PreferenceConcurrencyRetry.ClassifierDecision decision = PreferenceConcurrencyRetry.Classify(sqliteEx);

        decision.Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.Rethrow);
    }

    /// <summary>
    /// SQLite FK / NOT-NULL / CHECK failures MUST NOT retry — those are
    /// real semantic errors that a retry loop can only worsen.
    /// </summary>
    [Fact]
    public void Classify_SqliteForeignKeyOrCheckFailure_Rethrow()
    {
        // 787 = SQLITE_CONSTRAINT_FOREIGNKEY.
        var fkEx = new SqliteException("FK violation", 19, 787);
        PreferenceConcurrencyRetry.ClassifierDecision fkDecision = PreferenceConcurrencyRetry.Classify(fkEx);
        fkDecision.Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.Rethrow);

        // 275 = SQLITE_CONSTRAINT_CHECK.
        var checkEx = new SqliteException("CHECK violation", 19, 275);
        PreferenceConcurrencyRetry.ClassifierDecision checkDecision = PreferenceConcurrencyRetry.Classify(checkEx);
        checkDecision.Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.Rethrow);
    }

    /// <summary>
    /// Classifier recognises PostgreSQL 40001 serialization failure through
    /// reflection on a test-double type whose <c>SqlState</c> property
    /// mirrors Npgsql's <c>PostgresException.SqlState</c>. Uses the reflection
    /// surface so the test project does not pull in Npgsql.
    /// </summary>
    /// <remarks>
    /// After Hicks post-merge #2, the outer <see cref="PreferenceConcurrencyRetry.Classify"/>
    /// gates provider dispatch on <see cref="System.Data.Common.DbException"/> +
    /// exact allow-listed <c>FullName</c>. The provider-specific test doubles
    /// intentionally do not spoof either signal, so the SQLSTATE / error-code
    /// behaviour is asserted directly against the sub-classifier
    /// (<see cref="PreferenceConcurrencyRetry.ClassifyNpgsql"/> etc.). The outer
    /// <see cref="PreferenceConcurrencyRetry.Classify"/> gate is verified
    /// separately by the <c>Classify_LookAlike*</c> tests further down.
    /// </remarks>
    [Fact]
    public void Classify_PostgresSerializationFailure_IsTransient()
    {
        var pg = new PostgresException(sqlState: "40001", constraintName: null, tableName: null, columnName: null);

        PreferenceConcurrencyRetry.ClassifierDecision? decision = PreferenceConcurrencyRetry.ClassifyNpgsql(pg);

        decision.Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.TransientProviderConflict);
    }

    /// <summary>
    /// PostgreSQL 23505 (unique_violation) on the NotificationPreferences_UserId
    /// index is a UserId conflict; on any other constraint it MUST NOT retry.
    /// </summary>
    [Fact]
    public void Classify_PostgresUniqueOnUserId_IsUserIdUniqueConflict()
    {
        var pg = new PostgresException(
            sqlState: "23505",
            constraintName: "IX_NotificationPreferences_UserId",
            tableName: "NotificationPreferences",
            columnName: "UserId");

        PreferenceConcurrencyRetry.ClassifierDecision? decision = PreferenceConcurrencyRetry.ClassifyNpgsql(pg);

        decision.Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.UserIdUniqueConflict);
    }

    /// <summary>
    /// PostgreSQL 23505 on an UNRELATED constraint (e.g., some third-party
    /// migration adds a unique index on a different column) MUST NOT retry.
    /// </summary>
    [Fact]
    public void Classify_PostgresUniqueOnUnrelatedConstraint_Rethrow()
    {
        var pg = new PostgresException(
            sqlState: "23505",
            constraintName: "IX_Something_Else",
            tableName: "SomeOther",
            columnName: "SomeColumn");

        PreferenceConcurrencyRetry.ClassifierDecision? decision = PreferenceConcurrencyRetry.ClassifyNpgsql(pg);

        // Sub-classifier returns null when the constraint does not reference
        // the UserId unique index; the outer Classify would then fall through
        // to Rethrow for the whole exception chain.
        decision.Should().BeNull();
    }

    /// <summary>
    /// SQL Server deadlock victim (1205), snapshot conflict (3960), and lock
    /// timeout (1222) all classify as transient.
    /// </summary>
    [Theory]
    [InlineData(1205)]
    [InlineData(3960)]
    [InlineData(1222)]
    public void Classify_SqlServerTransientCodes_AreTransient(int number)
    {
        var ex = new SqlException(number, "conflict");

        PreferenceConcurrencyRetry.ClassifierDecision? decision =
            PreferenceConcurrencyRetry.ClassifySqlServer(ex, ex.Message);

        decision.Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.TransientProviderConflict);
    }

    /// <summary>
    /// SQL Server 2601 / 2627 duplicate-key against the UserId index →
    /// UserId conflict; against any other index → Rethrow.
    /// </summary>
    [Fact]
    public void Classify_SqlServerUniqueOnUserIdIndex_IsUserIdUniqueConflict()
    {
        var ex = new SqlException(2627, "Violation of UNIQUE KEY constraint IX_NotificationPreferences_UserId.");

        PreferenceConcurrencyRetry.ClassifierDecision? decision =
            PreferenceConcurrencyRetry.ClassifySqlServer(ex, ex.Message);

        decision.Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.UserIdUniqueConflict);
    }

    /// <summary>
    /// MySQL / MariaDB 1213 (deadlock) → transient. 1062 duplicate entry on
    /// the UserId index → UserId conflict; on any other index → Rethrow.
    /// </summary>
    [Fact]
    public void Classify_MySqlDeadlockAndUniqueOnUserId()
    {
        var deadlock = new MySqlException(1213, "Deadlock found");
        PreferenceConcurrencyRetry.ClassifyMySql(deadlock, deadlock.Message)
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.TransientProviderConflict);

        var uniqueUserId = new MySqlException(1062, "Duplicate entry for key 'IX_NotificationPreferences_UserId'");
        PreferenceConcurrencyRetry.ClassifyMySql(uniqueUserId, uniqueUserId.Message)
            .Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.UserIdUniqueConflict);

        var uniqueUnrelated = new MySqlException(1062, "Duplicate entry for key 'ix_something_else'");
        PreferenceConcurrencyRetry.ClassifyMySql(uniqueUnrelated, uniqueUnrelated.Message)
            .Should().BeNull();
    }

    /// <summary>
    /// Hicks post-merge #2: an arbitrary <see cref="Exception"/> subclass whose
    /// short name happens to be <c>SqliteException</c> — but which does NOT
    /// derive from <see cref="System.Data.Common.DbException"/> — MUST NOT be
    /// treated as a provider signal by the outer classifier. The prior
    /// short-name switch would have matched it; the tightened classifier
    /// gates on both <c>DbException</c> and the exact allow-listed
    /// <c>FullName</c>.
    /// </summary>
    [Fact]
    public void Classify_LookAlikeSqliteException_NotDbException_Rethrows()
    {
        var spoof = new LookAlikeSqliteException("UNIQUE constraint failed: NotificationPreferences.UserId");

        PreferenceConcurrencyRetry.ClassifierDecision decision = PreferenceConcurrencyRetry.Classify(spoof);

        decision.Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.Rethrow);
    }

    /// <summary>
    /// Hicks post-merge #2: a <see cref="System.Data.Common.DbException"/>-
    /// derived class whose full name is NOT on the provider allow-list must
    /// also be rejected. The short name alone is not enough — the outer
    /// classifier compares <c>GetType().FullName</c> against the exact
    /// namespaced allow-list.
    /// </summary>
    [Fact]
    public void Classify_LookAlikeDbException_WrongFullName_Rethrows()
    {
        // FullName = "Farm.Web.Api.Tests.Services.Notifications.PreferenceConcurrencyRetryTests+LookAlikeDbException"
        var spoof = new LookAlikeDbException("UNIQUE constraint failed: NotificationPreferences.UserId");

        PreferenceConcurrencyRetry.ClassifierDecision decision = PreferenceConcurrencyRetry.Classify(spoof);

        decision.Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.Rethrow);
    }

    /// <summary>
    /// Hicks post-merge #2: a genuine EF <see cref="DbUpdateConcurrencyException"/>
    /// wrapped inside an arbitrary custom exception must still classify as
    /// transient — the classifier walks the entire <see cref="Exception.InnerException"/>
    /// chain, so the tightened family gate MUST NOT reject legitimate signals
    /// that arrive nested inside third-party wrappers.
    /// </summary>
    [Fact]
    public void Classify_ArbitraryWrapperAroundDbUpdateConcurrency_StillTransient()
    {
        var inner = new DbUpdateConcurrencyException("concurrency conflict");
        var wrapper = new ArbitraryWrapperException("wrapped by third-party middleware", inner);

        PreferenceConcurrencyRetry.ClassifierDecision decision = PreferenceConcurrencyRetry.Classify(wrapper);

        decision.Should().Be(PreferenceConcurrencyRetry.ClassifierDecision.TransientProviderConflict);
    }

    /// <summary>
    /// End-to-end: a transient failure on attempt 1 retries to success on
    /// attempt 2 with a FRESH DbContext instance. Bishop #12 explicitly
    /// requires fresh context per attempt so no tracker state leaks.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TransientOnAttempt1_RetriesToSuccessOnAttempt2()
    {
        using var factory = new CountingFactory();
        int attempts = 0;

        int result = await PreferenceConcurrencyRetry.ExecuteAsync<int>(
            factory,
            fallbackContext: null,
            operation: (ctx, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new DbUpdateConcurrencyException("transient");
                }

                return Task.FromResult(42);
            },
            logger: NullLogger.Instance,
            cancellationToken: CancellationToken.None);

        result.Should().Be(42);
        attempts.Should().Be(2, "the classifier must retry on a transient conflict and succeed on the second attempt");
        factory.CreatedContexts.Should().Be(2, "each attempt MUST get a fresh DbContext instance");
        factory.DistinctContexts.Count.Should().Be(2, "the two attempts must see two DIFFERENT DbContext references");
    }

    /// <summary>
    /// Expected UserId-unique conflict retries (first-create convergence).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UserIdUniqueOnAttempt1_Retries()
    {
        using var factory = new CountingFactory();
        int attempts = 0;

        int result = await PreferenceConcurrencyRetry.ExecuteAsync<int>(
            factory,
            fallbackContext: null,
            operation: (ctx, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new SqliteException(
                        "SQLite Error 19: 'UNIQUE constraint failed: NotificationPreferences.UserId'.",
                        19,
                        2067);
                }

                return Task.FromResult(7);
            },
            logger: NullLogger.Instance,
            cancellationToken: CancellationToken.None);

        result.Should().Be(7);
        attempts.Should().Be(2);
    }

    /// <summary>
    /// Unrelated UNIQUE / FK / CHECK violations MUST NOT retry — the caller
    /// gets the raw exception on the first surface.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnrelatedUniqueConstraint_DoesNotRetry()
    {
        using var factory = new CountingFactory();
        int attempts = 0;

        Func<Task> act = () => PreferenceConcurrencyRetry.ExecuteAsync<int>(
            factory,
            fallbackContext: null,
            operation: (ctx, _) =>
            {
                attempts++;
                throw new SqliteException(
                    "SQLite Error 19: 'UNIQUE constraint failed: SomeOther.SomeColumn'.",
                    19,
                    2067);
            },
            logger: NullLogger.Instance,
            cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<SqliteException>();
        attempts.Should().Be(1, "unrelated unique constraints MUST NOT trigger a retry");
    }

    /// <summary>
    /// FK violation → no retry. Same rule for CHECK / NOT NULL.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ForeignKeyViolation_DoesNotRetry()
    {
        using var factory = new CountingFactory();
        int attempts = 0;

        Func<Task> act = () => PreferenceConcurrencyRetry.ExecuteAsync<int>(
            factory,
            fallbackContext: null,
            operation: (ctx, _) =>
            {
                attempts++;
                throw new SqliteException("FK violation", 19, 787);
            },
            logger: NullLogger.Instance,
            cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<SqliteException>();
        attempts.Should().Be(1);
    }

    /// <summary>
    /// Repeated transient failures MUST stop at MaxAttempts and surface the
    /// last transient exception verbatim. This guards against unbounded
    /// retry storms under sustained provider contention.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_RepeatedTransient_StopsAtMaxAttempts()
    {
        using var factory = new CountingFactory();
        int attempts = 0;

        Func<Task> act = () => PreferenceConcurrencyRetry.ExecuteAsync<int>(
            factory,
            fallbackContext: null,
            operation: (ctx, _) =>
            {
                attempts++;
                throw new DbUpdateConcurrencyException($"transient #{attempts}");
            },
            logger: NullLogger.Instance,
            cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        attempts.Should().Be(PreferenceConcurrencyRetry.MaxAttempts,
            "the retry bound MUST cap runaway attempts");
    }

    /// <summary>
    /// Cancellation propagates unconditionally without consuming any retry
    /// budget. If the caller cancels at the entrance, no attempt runs.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_PreCancelledToken_ThrowsWithoutRunningAnyAttempt()
    {
        using var factory = new CountingFactory();
        int attempts = 0;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => PreferenceConcurrencyRetry.ExecuteAsync<int>(
            factory,
            fallbackContext: null,
            operation: (ctx, _) =>
            {
                attempts++;
                return Task.FromResult(0);
            },
            logger: NullLogger.Instance,
            cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(0, "a pre-cancelled token MUST NOT allow any attempt to run");
    }

    /// <summary>
    /// Cancellation raised INSIDE an attempt (OperationCanceledException
    /// surfacing from the operation) propagates unconditionally — it MUST
    /// NOT be classified as transient, MUST NOT be swallowed, and MUST NOT
    /// consume the retry budget beyond that single attempt.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_InnerOperationCanceledException_RethrowsWithoutRetry()
    {
        using var factory = new CountingFactory();
        int attempts = 0;

        Func<Task> act = () => PreferenceConcurrencyRetry.ExecuteAsync<int>(
            factory,
            fallbackContext: null,
            operation: (ctx, _) =>
            {
                attempts++;
                throw new OperationCanceledException("simulated inner cancel");
            },
            logger: NullLogger.Instance,
            cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(1, "OCE MUST propagate on the first surface without a retry");
    }

    /// <summary>
    /// Successful operation completes on attempt 1 with no retry — the
    /// happy path.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_HappyPath_CompletesOnFirstAttempt()
    {
        using var factory = new CountingFactory();

        int result = await PreferenceConcurrencyRetry.ExecuteAsync<int>(
            factory,
            fallbackContext: null,
            operation: (ctx, _) => Task.FromResult(99),
            logger: NullLogger.Instance,
            cancellationToken: CancellationToken.None);

        result.Should().Be(99);
        factory.CreatedContexts.Should().Be(1);
    }

    /// <summary>
    /// A test-double PostgreSQL-shaped exception that mirrors
    /// <c>Npgsql.PostgresException</c>'s reflection surface (SqlState,
    /// ConstraintName, TableName, ColumnName). The classifier reads these
    /// via reflection and matches on <c>GetType().Name == "PostgresException"</c>,
    /// so we name this nested type exactly to satisfy the switch.
    /// </summary>
    private sealed class PostgresException : Exception
    {
        public PostgresException(string sqlState, string? constraintName, string? tableName, string? columnName)
            : base($"PostgresException {sqlState}")
        {
            SqlState = sqlState;
            ConstraintName = constraintName;
            TableName = tableName;
            ColumnName = columnName;
        }

        // Reflection contract: the classifier reads these by property name.
        public string SqlState { get; }

        public string? ConstraintName { get; }

        public string? TableName { get; }

        public string? ColumnName { get; }
    }

    /// <summary>
    /// Test-double SQL Server-shaped exception mirroring
    /// <c>Microsoft.Data.SqlClient.SqlException</c>: Number property.
    /// The classifier's typeName switch matches on <c>GetType().Name ==
    /// "SqlException"</c>.
    /// </summary>
    private sealed class SqlException : Exception
    {
        public SqlException(int number, string message)
            : base(message)
        {
            Number = number;
        }

        public int Number { get; }
    }

    /// <summary>
    /// Test-double MySQL exception mirroring MySqlConnector's Number
    /// surface. Classifier matches on <c>GetType().Name == "MySqlException"</c>.
    /// </summary>
    private sealed class MySqlException : Exception
    {
        public MySqlException(int number, string message)
            : base(message)
        {
            Number = number;
        }

        public int Number { get; }
    }

    /// <summary>
    /// Hicks post-merge #2: an arbitrary <see cref="Exception"/> subclass whose
    /// short name happens to be <c>SqliteException</c> — used to prove the
    /// tightened outer classifier rejects short-name spoofing. Deliberately
    /// does NOT derive from <see cref="System.Data.Common.DbException"/> and
    /// its <c>FullName</c> is a Farm namespace, so both gate conditions fail.
    /// </summary>
    private sealed class LookAlikeSqliteException : Exception
    {
        public LookAlikeSqliteException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Hicks post-merge #2: derives from <see cref="System.Data.Common.DbException"/>
    /// so the base-type gate passes, but the <c>FullName</c> is a Farm test
    /// namespace and therefore NOT on the provider allow-list. Proves the
    /// classifier still rejects it.
    /// </summary>
    private sealed class LookAlikeDbException : System.Data.Common.DbException
    {
        public LookAlikeDbException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Hicks post-merge #2: an arbitrary non-provider wrapper that carries
    /// a legitimate inner exception. Used to prove the classifier still
    /// walks the whole <see cref="Exception.InnerException"/> chain after
    /// the family gate tightened.
    /// </summary>
    private sealed class ArbitraryWrapperException : Exception
    {
        public ArbitraryWrapperException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>
    /// Counting factory: creates a fresh <see cref="AppDbContext"/> backed
    /// by a per-test shared-cache SQLite database. Records how many contexts
    /// were created and each distinct reference so tests can prove
    /// fresh-context semantics.
    /// </summary>
    private sealed class CountingFactory : IDbContextFactory<AppDbContext>, IDisposable
    {
        private readonly SqliteConnection _keepAlive;
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly HashSet<AppDbContext> _distinct = new();

        public CountingFactory()
        {
            string connString = "Data Source=file:pcr-tests-" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(connString);
            _keepAlive.Open();
            _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connString).Options;
            using var seed = new AppDbContext(_options);
            seed.Database.EnsureCreated();
        }

        public int CreatedContexts { get; private set; }

        public IReadOnlyCollection<AppDbContext> DistinctContexts => _distinct;

        public AppDbContext CreateDbContext()
        {
            CreatedContexts++;
            var ctx = new AppDbContext(_options);
            _distinct.Add(ctx);
            return ctx;
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());

        public void Dispose()
        {
            _keepAlive.Dispose();
        }
    }

    /// <summary>Minimal NullLogger that supports the parameterised LogWarning call.</summary>
    private static class NullLogger
    {
        public static Microsoft.Extensions.Logging.ILogger Instance { get; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }
}
