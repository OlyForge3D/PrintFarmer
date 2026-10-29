using System;
using System.Collections.Concurrent;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Tests.Builders;
using Farm.Testing.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Npgsql;
using Xunit;

namespace Farm.Infrastructure.Tests.Dispatch;

/// <summary>
/// Regression coverage for #3604: SQLite lock contention during the auto-dispatch durable
/// reconciliation scan escaped <c>ExecuteAsync</c> and stopped the API host under the default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c>. These tests drive the real hosted loop
/// against a file-backed SQLite database in its default rollback-journal mode while another
/// connection holds <c>BEGIN EXCLUSIVE</c>, so the scan observes the genuine provider failure.
/// </summary>
public sealed class AutoDispatchDurableScanContentionTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly string _databasePath = Path.Join(
        Path.GetTempPath(),
        $"autodispatch-contention-{Guid.NewGuid():N}.db");
    private readonly AutoDispatchTrigger _trigger = new();
    private readonly DispatchConcurrencyCoordinator _concurrencyCoordinator = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2031, 4, 5, 6, 7, 8, TimeSpan.Zero));
    private readonly Mock<IHubContext<PrinterHub>> _hubMock = new();
    private readonly Mock<IDispatchScorer> _scorerMock = new();
    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private readonly SemaphoreSlim _scanContentionLogged = new(0);
    private readonly ThrowingReaderInterceptor _interceptor = new();
    private readonly TaskCompletionSource<Guid> _scored = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ServiceProvider _provider = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        // Default Timeout=1 bounds Microsoft.Data.Sqlite's busy wait so the genuine SQLITE_BUSY
        // surfaces in about a second instead of the production 30s command timeout.
        _connectionString = $"Data Source={_databasePath};Pooling=False;Default Timeout=1";

        var clientProxy = new Mock<IClientProxy>();
        var hubClients = new Mock<IHubClients>();
        hubClients.Setup(value => value.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        _hubMock.Setup(value => value.Clients).Returns(hubClients.Object);

        _scorerMock
            .Setup(value => value.ScorePrinterForJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, CancellationToken>((_, printerId, _) => _scored.TrySetResult(printerId))
            .ReturnsAsync((DispatchScore?)null);

        var services = new ServiceCollection();
        services.AddScoped(_ => CreateContext());
        services.AddScoped(_ => _scorerMock.Object);
        services.AddScoped(_ => Mock.Of<IJobDispatchService>(MockBehavior.Strict));
        services.AddScoped(_ => Mock.Of<IAutoDispatchService>(MockBehavior.Strict));
        _provider = services.BuildServiceProvider();

        // Mirror production startup: DatabaseInitializationExtensions.HasLegacySqliteSchemaAsync
        // opens the connection (creating the file) before migrations run, so EF never takes its
        // fresh-database Create() path that would switch the file to WAL. The database therefore
        // keeps SQLite's rollback-journal default, where an exclusive writer blocks readers.
        await using (SqliteConnection connection = new(_connectionString))
        {
            await connection.OpenAsync();
        }

        await using AppDbContext db = CreateContext();
        await db.Database.EnsureCreatedAsync();

        await using (SqliteConnection connection = new(_connectionString))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            (await command.ExecuteScalarAsync()).Should().Be("delete");
        }
    }

    public Task DisposeAsync()
    {
        Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
        File.Delete(_databasePath + "-journal");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _concurrencyCoordinator.Dispose();
        _scanContentionLogged.Dispose();
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ExecuteAsync_RealSqliteLockDuringStartupAndPeriodicScans_KeepsRunningAndRecoversOnNextScan()
    {
        Guid printerId = await SeedEligiblePrinterWithQueuedJobAsync();
        using AutoDispatchBackgroundService service = CreateService();

        await using (SqliteConnection lockHolder = await HoldExclusiveLockAsync())
        {
            await service.StartAsync(CancellationToken.None);

            LogEntry startupFailure = await WaitForScanContentionLogAsync();
            startupFailure.Level.Should().Be(LogLevel.Warning);
            startupFailure.Exception.Should().BeOfType<SqliteException>()
                .Which.SqliteErrorCode.Should().Be(5, "the held exclusive lock must surface as genuine SQLITE_BUSY");
            service.ExecuteTask!.IsCompleted.Should().BeFalse("transient contention must not end the hosted service");

            // The production crash was in the periodic branch, so prove that path too.
            await AdvanceToNextScanAsync();
            LogEntry periodicFailure = await WaitForScanContentionLogAsync();
            periodicFailure.Level.Should().Be(LogLevel.Warning);
            periodicFailure.Exception.Should().BeOfType<SqliteException>()
                .Which.SqliteErrorCode.Should().Be(5);
            service.ExecuteTask.IsCompleted.Should().BeFalse();
            _scored.Task.IsCompleted.Should().BeFalse("no dispatch work can be selected while the scan is blocked");

            await using SqliteCommand commit = lockHolder.CreateCommand();
            commit.CommandText = "COMMIT;";
            await commit.ExecuteNonQueryAsync();
        }

        await AdvanceToNextScanAsync();
        Guid scoredPrinter = await _scored.Task.WaitAsync(WaitTimeout);

        scoredPrinter.Should().Be(printerId, "the next durable scan must re-queue the eligible printer once the lock clears");
        _logs.Should().Contain(entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains("recovered after 2 contention failure(s)"));
        service.ExecuteTask.IsCompleted.Should().BeFalse();

        await service.StopAsync(CancellationToken.None).WaitAsync(WaitTimeout);
        service.ExecuteTask.Status.Should().Be(TaskStatus.RanToCompletion);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ExecuteAsync_StopWhileLockContentionPersists_CompletesWithoutFaultOrDispatch()
    {
        await SeedEligiblePrinterWithQueuedJobAsync();
        using AutoDispatchBackgroundService service = CreateService();

        await using SqliteConnection lockHolder = await HoldExclusiveLockAsync();
        await service.StartAsync(CancellationToken.None);
        (await WaitForScanContentionLogAsync()).Exception.Should().BeOfType<SqliteException>();
        await _clock.WaitForActiveTimersAsync(1, WaitTimeout);

        await service.StopAsync(CancellationToken.None).WaitAsync(WaitTimeout);

        service.ExecuteTask!.Status.Should().Be(TaskStatus.RanToCompletion);
        _scored.Task.IsCompleted.Should().BeFalse();
        _logs.Should().NotContain(entry => entry.Level >= LogLevel.Critical);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ExecuteAsync_NonTransientDatabaseFailure_StillFaultsTheService()
    {
        await SeedEligiblePrinterWithQueuedJobAsync();
        string table;
        await using (AppDbContext db = CreateContext())
        {
            table = db.Model.FindEntityType(typeof(DispatchSettings))!.GetTableName()!;
        }

        await using (SqliteConnection connection = new(_connectionString))
        {
            await connection.OpenAsync();
            await using SqliteCommand drop = connection.CreateCommand();
            drop.CommandText = $"DROP TABLE \"{table}\";";
            await drop.ExecuteNonQueryAsync();
        }

        using AutoDispatchBackgroundService service = CreateService();

        // StartAsync surfaces the fault directly when ExecuteAsync completes synchronously;
        // otherwise ExecuteTask carries it. Either way it must not be swallowed.
        Func<Task> run = async () =>
        {
            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!.WaitAsync(WaitTimeout);
        };

        (await run.Should().ThrowAsync<SqliteException>())
            .Which.SqliteErrorCode.Should().Be(1, "a missing table is SQLITE_ERROR, not contention");
        service.ExecuteTask!.IsFaulted.Should().BeTrue();
        _logs.Should().NotContain(entry => entry.Message.Contains("Durable reconciliation scan"));
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task RunDurableScanAsync_ConsecutiveContention_EscalatesToErrorAndResetsAfterRecovery()
    {
        await SeedEligiblePrinterWithQueuedJobAsync();
        using AutoDispatchBackgroundService service = CreateService();
        using CancellationTokenSource cts = new(WaitTimeout);

        _interceptor.Failure = new SqliteException("database is locked", 5);
        for (int attempt = 1; attempt <= AutoDispatchBackgroundService.ConsecutiveContentionErrorThreshold; attempt++)
        {
            await service.RunDurableScanAsync(cts.Token);
        }

        _interceptor.Failure = null;
        await service.RunDurableScanAsync(cts.Token);
        _interceptor.Failure = new SqliteException("database table is locked", 6);
        await service.RunDurableScanAsync(cts.Token);

        _logs.Where(entry => entry.Message.Contains("Durable reconciliation scan"))
            .Select(entry => entry.Level)
            .Should().Equal(
                LogLevel.Warning,
                LogLevel.Warning,
                LogLevel.Error,
                LogLevel.Information,
                LogLevel.Warning);
        _trigger.IntentStateCount.Should().Be(1, "only the successful scan re-queues the eligible printer");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task RunDurableScanAsync_CancelledDuringContention_PropagatesCancellation()
    {
        await SeedEligiblePrinterWithQueuedJobAsync();
        using AutoDispatchBackgroundService service = CreateService();
        using CancellationTokenSource cts = new();
        _interceptor.OnExecuting = cts.Cancel;
        _interceptor.Failure = new SqliteException("database is locked", 5);

        Func<Task> scan = async () => await service.RunDurableScanAsync(cts.Token);

        await scan.Should().ThrowAsync<SqliteException>(
            "contention observed after shutdown was requested must not be reported as recovered work");
        _logs.Should().BeEmpty();
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(1, false)]
    [InlineData(11, false)]
    [InlineData(19, false)]
    public void IsTransientContention_ClassifiesSqliteByErrorCode(int errorCode, bool expected)
    {
        DispatchTransientDatabaseFailure.IsTransientContention(new SqliteException("sqlite", errorCode))
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.SerializationFailure, true)]
    [InlineData(PostgresErrorCodes.DeadlockDetected, true)]
    [InlineData(PostgresErrorCodes.LockNotAvailable, true)]
    [InlineData(PostgresErrorCodes.UniqueViolation, false)]
    [InlineData(PostgresErrorCodes.UndefinedTable, false)]
    public void IsTransientContention_ClassifiesPostgresBySqlState(string sqlState, bool expected)
    {
        DispatchTransientDatabaseFailure.IsTransientContention(
                new PostgresException("postgres", "ERROR", "ERROR", sqlState))
            .Should().Be(expected);
    }

    [Fact]
    public void IsTransientContention_RejectsWrappedAndUnrelatedExceptions()
    {
        var locked = new SqliteException("database is locked", 5);

        DispatchTransientDatabaseFailure.IsTransientContention(new InvalidOperationException("wrapped", locked))
            .Should().BeFalse();
        DispatchTransientDatabaseFailure.IsTransientContention(new DbUpdateException("wrapped", locked))
            .Should().BeFalse();
        DispatchTransientDatabaseFailure.IsTransientContention(new TimeoutException())
            .Should().BeFalse();
        DispatchTransientDatabaseFailure.IsTransientContention(new OperationCanceledException())
            .Should().BeFalse();
    }

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(_interceptor)
            .Options);

    private AutoDispatchBackgroundService CreateService() =>
        new(
            _trigger,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _concurrencyCoordinator,
            _hubMock.Object,
            new RecordingLogger(this),
            _clock);

    private async Task<SqliteConnection> HoldExclusiveLockAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using SqliteCommand begin = connection.CreateCommand();
        begin.CommandText = "BEGIN EXCLUSIVE;";
        await begin.ExecuteNonQueryAsync();
        return connection;
    }

    private async Task AdvanceToNextScanAsync()
    {
        await _clock.WaitForActiveTimersAsync(1, WaitTimeout);
        _clock.Advance(ScanInterval);
    }

    private async Task<LogEntry> WaitForScanContentionLogAsync()
    {
        (await _scanContentionLogged.WaitAsync(WaitTimeout))
            .Should().BeTrue("the blocked scan must report its contention");
        return _logs.Last(entry => entry.Exception is not null && entry.Message.Contains("Durable reconciliation scan"));
    }

    private async Task<Guid> SeedEligiblePrinterWithQueuedJobAsync()
    {
        await using AppDbContext db = CreateContext();
        DispatchSettings? settings = await db.DispatchSettings.FirstOrDefaultAsync();
        if (settings is null)
        {
            settings = new DispatchSettings { Id = 1 };
            db.DispatchSettings.Add(settings);
        }

        settings.AutoDispatchEnabled = true;
        settings.AutoDispatchMode = AutoDispatchMode.Auto;
        settings.IdleThresholdSeconds = 0;
        settings.MinimumScoreThreshold = 0.5;
        settings.MaxConcurrentDispatches = 3;
        settings.UpdatedAt = DateTime.UtcNow;

        var folder = new FolderNode { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode", CreatedAt = DateTime.UtcNow };
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"Mfg-{Guid.NewGuid():N}" };
        var model = new PrinterModel { Id = Guid.NewGuid(), Name = "Model", ManufacturerId = manufacturer.Id };
        Printer printer = new PrinterBuilder()
            .WithId(Guid.NewGuid())
            .WithName("Contention Printer")
            .WithServerUrl("http://192.168.1.10")
            .Build();
        printer.ManufacturerId = manufacturer.Id;
        printer.ModelId = model.Id;
        printer.IsEnabled = true;
        printer.IsAvailable = true;
        printer.AutoDispatchEnabled = true;
        printer.DispatchState = new PrinterDispatchState { PrinterId = printer.Id, AutoDispatchState = AutoDispatchState.Ready };

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "contention.gcode",
            FileName = $"{Guid.NewGuid()}.gcode",
            FilePath = "/gcode/",
            FolderId = folder.Id,
            FileHash = "deadbeef",
            UploadedAt = DateTime.UtcNow,
        };
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "contention-job",
            GcodeFileId = gcode.Id,
            GcodeFile = gcode,
            Status = PrintJobStatus.Queued,
            QueuePosition = 1,
            QueuedAt = _clock.GetUtcNow().UtcDateTime.AddMinutes(-1),
        };

        db.AddRange(folder, manufacturer, model, printer, gcode, job);
        await db.SaveChangesAsync();
        return printer.Id;
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger(AutoDispatchDurableScanContentionTests owner)
        : ILogger<AutoDispatchBackgroundService>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            owner._logs.Enqueue(new LogEntry(logLevel, message, exception));
            if (exception is not null && message.Contains("Durable reconciliation scan"))
            {
                owner._scanContentionLogged.Release();
            }
        }
    }

    /// <summary>Throws a configured provider exception before any reader command executes.</summary>
    private sealed class ThrowingReaderInterceptor : DbCommandInterceptor
    {
        public Exception? Failure { get; set; }

        public Action? OnExecuting { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Failure is Exception failure)
            {
                OnExecuting?.Invoke();
                throw failure;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
