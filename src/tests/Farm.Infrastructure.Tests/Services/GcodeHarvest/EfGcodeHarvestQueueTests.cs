using System.Data.Common;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.GcodeHarvest;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.GcodeHarvest;

public sealed class EfGcodeHarvestQueueTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly QueueReadInterceptor _interceptor = new();
    private readonly Guid _printerId = Guid.NewGuid();
    private readonly Guid _otherPrinterId = Guid.NewGuid();
    private readonly DateTime _queuedAt = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private AppDbContext _db = null!;
    private EfGcodeHarvestQueue _queue = null!;
    private GcodeHarvestQueueItem[] _items = [];

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_interceptor)
            .Options;
        _db = new AppDbContext(options);
        await _db.Database.EnsureCreatedAsync();
        _queue = new EfGcodeHarvestQueue(_db, NullLogger<EfGcodeHarvestQueue>.Instance);

        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Test manufacturer" };
        var model = new PrinterModel { Id = Guid.NewGuid(), Name = "Test model", ManufacturerId = manufacturer.Id };
        _db.Manufacturers.Add(manufacturer);
        _db.PrinterModels.Add(model);
        _db.Printers.AddRange(
            new Printer
            {
                Id = _printerId,
                Name = "Workshop printer",
                ServerUrl = "http://printer-a:7125",
                ManufacturerId = manufacturer.Id,
                ModelId = model.Id
            },
            new Printer
            {
                Id = _otherPrinterId,
                Name = "Other printer",
                ServerUrl = "http://printer-b:7125",
                ManufacturerId = manufacturer.Id,
                ModelId = model.Id
            });

        _items =
        [
            CreateItem(_printerId, GcodeHarvestQueueItemStatus.Pending, 1, _queuedAt),
            CreateItem(_printerId, GcodeHarvestQueueItemStatus.Completed, 3, _queuedAt.AddMinutes(1)),
            CreateItem(_otherPrinterId, GcodeHarvestQueueItemStatus.Pending, 3, _queuedAt),
            CreateItem(_printerId, GcodeHarvestQueueItemStatus.Pending, 3, _queuedAt.AddMinutes(2))
        ];
        _db.GcodeHarvestQueueItems.AddRange(_items);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _interceptor.Commands.Clear();
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, GcodeHarvestQueueItemStatus.Pending)]
    [InlineData(true, GcodeHarvestQueueItemStatus.Pending)]
    [InlineData(true, GcodeHarvestQueueItemStatus.Completed)]
    [InlineData(true, GcodeHarvestQueueItemStatus.Failed)]
    public async Task GetQueuedItemsAsync_OptionalFilters_ReturnsMatchingRowsInQueueOrder(
        bool filterPrinter,
        GcodeHarvestQueueItemStatus? status)
    {
        Guid? printerId = filterPrinter ? _printerId : null;

        IReadOnlyList<GcodeHarvestQueueItemDto> result = await _queue.GetQueuedItemsAsync(status, printerId);

        Guid[] expected = _items
            .Where(item => (!printerId.HasValue || item.PrinterId == printerId)
                && (!status.HasValue || item.Status == status))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.QueuedAt)
            .Select(item => item.Id)
            .ToArray();
        Assert.Equal(expected, result.Select(item => item.Id));
        Assert.Empty(_db.ChangeTracker.Entries());

        string sql = Assert.Single(_interceptor.Commands);
        Assert.Equal(filterPrinter || status.HasValue, sql.Contains("WHERE", StringComparison.Ordinal));
        if (filterPrinter)
        {
            Assert.Contains("\"PrinterId\" =", sql);
        }

        if (status.HasValue)
        {
            Assert.Contains("\"Status\" =", sql);
        }
    }

    [Fact]
    public async Task GetQueuedItemsAsync_UnknownPrinter_ReturnsEmpty()
    {
        IReadOnlyList<GcodeHarvestQueueItemDto> result = await _queue.GetQueuedItemsAsync(printerId: Guid.NewGuid());

        Assert.Empty(result);
        Assert.Single(_interceptor.Commands);
    }

    [Fact]
    public async Task GetQueuedItemsAsync_PrinterFilter_UsesExistingPrinterIndex()
    {
        await _queue.GetQueuedItemsAsync(printerId: _printerId);

        await using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + Assert.Single(_interceptor.Commands);
        foreach ((string name, object? value) in _interceptor.Parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var plan = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(3));
        }

        Assert.Contains(plan, step => step.Contains(
            "USING INDEX IX_GcodeHarvestQueueItems_PrinterId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetQueuedItemsAsync_FreshContext_ProjectsSummaryAndPrinterNameWithoutTrackingEntities()
    {
        IReadOnlyList<GcodeHarvestQueueItemDto> result = await _queue.GetQueuedItemsAsync(
            GcodeHarvestQueueItemStatus.Completed, _printerId);

        GcodeHarvestQueueItem item = _items[1];
        Assert.Equal(new GcodeHarvestQueueItemDto(
            item.Id,
            _printerId,
            "Workshop printer",
            item.QueuedAt,
            item.ProcessingStartedAt,
            item.CompletedAt,
            item.Status,
            item.Priority,
            item.ErrorMessage,
            item.FilesFound,
            item.FilesAdded), Assert.Single(result));
        Assert.Empty(_db.ChangeTracker.Entries());

        string sql = Assert.Single(_interceptor.Commands);
        Assert.Contains("JOIN \"Printers\"", sql);
        Assert.Contains(".\"Name\"", sql);
        Assert.DoesNotContain("\"Parameters\"", sql);
        Assert.DoesNotContain("\"ErrorDetails\"", sql);
        Assert.DoesNotContain("\"FilesSkipped\"", sql);
        Assert.DoesNotContain("\"FilesErrored\"", sql);
        Assert.DoesNotContain("\"ServerUrl\"", sql);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static GcodeHarvestQueueItem CreateItem(
        Guid printerId, GcodeHarvestQueueItemStatus status, int priority, DateTime queuedAt)
    {
        return new GcodeHarvestQueueItem
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            Status = status,
            Priority = priority,
            QueuedAt = queuedAt,
            ProcessingStartedAt = status == GcodeHarvestQueueItemStatus.Completed ? queuedAt.AddSeconds(1) : null,
            CompletedAt = status == GcodeHarvestQueueItemStatus.Completed ? queuedAt.AddSeconds(2) : null,
            Parameters = "{\"fileExtensions\":[\"gcode\"]}",
            ErrorMessage = "summary message",
            ErrorDetails = "private diagnostic details",
            FilesFound = 7,
            FilesAdded = 5,
            FilesSkipped = 1,
            FilesErrored = 1
        };
    }

    private sealed class QueueReadInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public List<(string Name, object? Value)> Parameters { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            Parameters.Clear();
            foreach (DbParameter parameter in command.Parameters)
            {
                Parameters.Add((parameter.ParameterName, parameter.Value));
            }

            return ValueTask.FromResult(result);
        }
    }
}
