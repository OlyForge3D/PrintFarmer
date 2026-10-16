using System.Data.Common;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Farm.Infrastructure.Tests.Repositories.Maintenance;

/// <summary>
/// Relational SQLite coverage for <see cref="EfMaintenanceLogRepository.GetPrinterLogsAsync"/> and
/// <see cref="EfMaintenanceLogRepository.CountPrinterLogsAsync"/> (issue #3297): the limit and
/// feature-gate filter run in the database, ordering is deterministic, and nothing is change-tracked.
/// </summary>
public sealed class EfMaintenanceLogRepositoryPrinterLogsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly CommandCapture _capture = new();
    private readonly AppDbContext _db;
    private readonly EfMaintenanceLogRepository _repo;
    private readonly Guid _printerId = Guid.NewGuid();
    private readonly Guid _otherPrinterId = Guid.NewGuid();
    private readonly Guid _toolheadId = Guid.NewGuid();
    private readonly Guid _taskId = Guid.NewGuid();

    public EfMaintenanceLogRepositoryPrinterLogsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        _connection.Open();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_capture)
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _repo = new EfMaintenanceLogRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private MaintenanceLog AddLog(string name, DateTime performedAt, Guid? toolheadId = null, Guid? printerId = null)
    {
        MaintenanceLog log = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId ?? _printerId,
            ToolheadId = toolheadId,
            MaintenanceTaskId = _taskId,
            TaskName = name,
            PerformedAt = performedAt,
            CreatedAt = performedAt,
        };
        _db.MaintenanceLogs.Add(log);
        return log;
    }

    /// <summary>
    /// Seeds five logs for the printer (three sharing one timestamp) plus a newer log on another
    /// printer that must never leak in. Returns the printer's logs.
    /// </summary>
    private async Task<List<MaintenanceLog>> SeedAsync()
    {
        _db.MaintenanceTasks.Add(new MaintenanceTask { Id = _taskId, TaskName = "Lubricate rails", Category = "Motion" });
        List<MaintenanceLog> logs =
        [
            AddLog("older-wide", Now.AddDays(-1)),
            AddLog("tie-wide-1", Now),
            AddLog("tie-tool", Now, _toolheadId),
            AddLog("tie-wide-2", Now),
            AddLog("oldest-tool", Now.AddDays(-2), _toolheadId),
        ];
        AddLog("other-printer", Now.AddDays(1), printerId: _otherPrinterId);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _capture.Commands.Clear();
        return logs;
    }

    // EF Core's SQLite provider stores Guids as upper-case TEXT, so "Id descending" is the
    // ordinal order of that text. Ties on PerformedAt must follow it.
    private static IEnumerable<Guid> ExpectedOrder(IEnumerable<MaintenanceLog> logs) =>
        logs.OrderByDescending(l => l.PerformedAt)
            .ThenByDescending(l => l.Id.ToString().ToUpperInvariant(), StringComparer.Ordinal)
            .Select(l => l.Id);

    [Fact]
    public async Task NoLimit_ReturnsWholeHistoryForPrinter_MostRecentFirstWithIdTiebreak()
    {
        List<MaintenanceLog> seeded = await SeedAsync();

        List<MaintenanceLogDto> result = await _repo.GetPrinterLogsAsync(new MaintenanceLogListQuery(_printerId));

        result.Select(l => l.Id).Should().Equal(ExpectedOrder(seeded));
        result.Should().OnlyContain(l => l.PrinterId == _printerId);
        result.Should().OnlyContain(l => l.MaintenanceTask != null && l.MaintenanceTask.Id == _taskId);
        _capture.Commands.Should().ContainSingle().Which.Should().NotContain("LIMIT");
    }

    [Fact]
    public async Task Limit_IsAppliedInSql_AndReturnsMostRecentRowsDeterministically()
    {
        List<MaintenanceLog> seeded = await SeedAsync();

        List<MaintenanceLogDto> first = await _repo.GetPrinterLogsAsync(new MaintenanceLogListQuery(_printerId, Limit: 2));
        List<MaintenanceLogDto> second = await _repo.GetPrinterLogsAsync(new MaintenanceLogListQuery(_printerId, Limit: 2));

        // Both rows come from the three-way timestamp tie, so only the Id tiebreaker decides them.
        first.Select(l => l.Id).Should().Equal(ExpectedOrder(seeded).Take(2));
        second.Select(l => l.Id).Should().Equal(first.Select(l => l.Id));
        _capture.Commands.Should().HaveCount(2).And.OnlyContain(sql => sql.Contains("LIMIT"));
    }

    [Fact]
    public async Task PrinterWideOnly_ReturnsOnlyUnscopedLogs_AndCountIgnoresLimit()
    {
        List<MaintenanceLog> seeded = await SeedAsync();
        MaintenanceLogListQuery query = new(_printerId, PrinterWideOnly: true, Limit: 1);

        List<MaintenanceLogDto> result = await _repo.GetPrinterLogsAsync(query);
        int total = await _repo.CountPrinterLogsAsync(query);

        result.Select(l => l.Id).Should().Equal(ExpectedOrder(seeded.Where(l => l.ToolheadId == null)).Take(1));
        total.Should().Be(3);
    }

    [Fact]
    public async Task ReadPath_DoesNotTrackAnyEntities()
    {
        await SeedAsync();

        await _repo.GetPrinterLogsAsync(new MaintenanceLogListQuery(_printerId, Limit: 10));

        _db.ChangeTracker.Entries().Should().BeEmpty();
    }

    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"MaintenanceLogs\"", StringComparison.Ordinal))
            {
                Commands.Add(command.CommandText);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
