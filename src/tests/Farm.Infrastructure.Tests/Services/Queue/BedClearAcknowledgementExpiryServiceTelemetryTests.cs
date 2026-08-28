// <copyright file="BedClearAcknowledgementExpiryServiceTelemetryTests.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Tests.Builders;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Queue;

/// <summary>
/// Telemetry coverage for <see cref="BedClearAcknowledgementExpiryService"/>'s scan loop, per
/// issue #1732: the scanned-printer count and per-pass duration must be observable via the
/// standard <see cref="System.Diagnostics.Metrics"/> instruments so a future round can measure
/// the real acknowledged-printer distribution without a one-off profiling spike.
/// Explicitly out of scope: the batched-query rewrite, which the owner deferred.
/// </summary>
public class BedClearAcknowledgementExpiryServiceTelemetryTests : IDisposable
{
    private sealed class FakeBedClearAcknowledgementService : IBedClearAcknowledgementService
    {
        public List<Guid> InvalidatedPrinterIds { get; } = new();

        public Task<AcknowledgeBedClearResult> AcknowledgeAsync(
            AcknowledgeBedClearRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by these telemetry tests.");

        public Task InvalidateStaleAcknowledgementsAsync(Guid printerId, CancellationToken ct = default)
        {
            InvalidatedPrinterIds.Add(printerId);
            return Task.CompletedTask;
        }
    }

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ServiceProvider _sp;
    private readonly BedClearAcknowledgementExpiryMetrics _metrics;
    private readonly FakeBedClearAcknowledgementService _fakeAckService = new();

    public BedClearAcknowledgementExpiryServiceTelemetryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _metrics = new BedClearAcknowledgementExpiryMetrics();

        ServiceCollection services = new();
        services.AddScoped<AppDbContext>(_ =>
        {
            var opts = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new AppDbContext(opts);
        });
        services.AddScoped<IBedClearAcknowledgementService>(_ => _fakeAckService);

        _sp = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _db.Dispose();
        _sp.Dispose();
        _connection.Dispose();
        _metrics.Dispose();
        GC.SuppressFinalize(this);
    }

    private BedClearAcknowledgementExpiryService CreateSut() =>
        new(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BedClearAcknowledgementExpiryService>.Instance,
            _metrics);

    private Guid SeedAcknowledgedPrinter(int index)
    {
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        _db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"TestMfg-{Guid.NewGuid():N}" });
        _db.SaveChanges();

        _db.PrinterModels.Add(new PrinterModel { Id = modelId, Name = $"TestModel-{index}", ManufacturerId = manufacturerId });
        _db.SaveChanges();

        Printer printer = new PrinterBuilder()
            .WithId(Guid.NewGuid())
            .WithName($"Printer-{index}")
            .WithServerUrl($"http://192.168.1.{index}")
            .Build();
        printer.ManufacturerId = manufacturerId;
        printer.ModelId = modelId;
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AcknowledgedJobId = Guid.NewGuid(),
            AcknowledgedAtUtc = DateTime.UtcNow,
        };

        _db.Printers.Add(printer);
        _db.SaveChanges();
        return printer.Id;
    }

    private async Task<(List<int> counts, List<double> durations)> ListenForScanMetricsAsync(Func<Task> recordEvents)
    {
        List<int> counts = new();
        List<double> durations = new();

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            // Filter by instrument identity, not by Meter.Name string equality: another live
            // instance of BedClearAcknowledgementExpiryMetrics (e.g. a DI-registered singleton
            // spun up elsewhere in a parallel test run) would share the same meter name and
            // instrument names, and a name-based filter would silently pick up its
            // measurements too, making these assertions flaky under this assembly's enabled
            // parallel test execution.
            if (instrument == _metrics.ScannedCount || instrument == _metrics.ScanDurationMs)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, measurement, _, _) =>
        {
            if (instrument == _metrics.ScannedCount)
            {
                counts.Add(measurement);
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (instrument == _metrics.ScanDurationMs)
            {
                durations.Add(measurement);
            }
        });
        listener.Start();

        await recordEvents();

        return (counts, durations);
    }

    [Fact(DisplayName = "ScanAsync records scanned count and duration once per pass")]
    public async Task ScanAsync_RecordsScannedCountAndDuration_OncePerPass()
    {
        Guid printerA = SeedAcknowledgedPrinter(1);
        Guid printerB = SeedAcknowledgedPrinter(2);
        Guid printerC = SeedAcknowledgedPrinter(3);

        BedClearAcknowledgementExpiryService sut = CreateSut();

        (List<int> counts, List<double> durations) = await ListenForScanMetricsAsync(() =>
            sut.ScanAsync(CancellationToken.None));

        _ = counts.Should().ContainSingle().Which.Should().Be(3, "exactly 3 acknowledged printers were scanned");
        _ = durations.Should().ContainSingle();
        _ = durations[0].Should().BeGreaterThanOrEqualTo(0, "duration must never be negative");

        _ = _fakeAckService.InvalidatedPrinterIds.Should().BeEquivalentTo(new[] { printerA, printerB, printerC });
    }

    [Fact(DisplayName = "ScanAsync records a zero scanned count when nothing is acknowledged")]
    public async Task ScanAsync_RecordsZeroScannedCount_WhenNoAcknowledgedPrinters()
    {
        BedClearAcknowledgementExpiryService sut = CreateSut();

        (List<int> counts, List<double> durations) = await ListenForScanMetricsAsync(() =>
            sut.ScanAsync(CancellationToken.None));

        _ = counts.Should().ContainSingle().Which.Should().Be(0, "no printers had an outstanding acknowledgement");
        _ = durations.Should().ContainSingle();
        _ = _fakeAckService.InvalidatedPrinterIds.Should().BeEmpty();
    }

    [Fact(DisplayName = "ScanAsync only scans printers with an outstanding acknowledgement")]
    public async Task ScanAsync_OnlyScansPrintersWithOutstandingAcknowledgement()
    {
        Guid acknowledged = SeedAcknowledgedPrinter(1);

        // A printer with no outstanding acknowledgement must not be scanned.
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        _db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"TestMfg-{Guid.NewGuid():N}" });
        _db.SaveChanges();
        _db.PrinterModels.Add(new PrinterModel { Id = modelId, Name = "TestModel-unacked", ManufacturerId = manufacturerId });
        _db.SaveChanges();
        Printer unacked = new PrinterBuilder()
            .WithId(Guid.NewGuid())
            .WithName("Unacked Printer")
            .WithServerUrl("http://192.168.1.99")
            .Build();
        unacked.ManufacturerId = manufacturerId;
        unacked.ModelId = modelId;
        unacked.DispatchState = new PrinterDispatchState { PrinterId = unacked.Id };
        _db.Printers.Add(unacked);
        _db.SaveChanges();

        BedClearAcknowledgementExpiryService sut = CreateSut();

        (List<int> counts, _) = await ListenForScanMetricsAsync(() =>
            sut.ScanAsync(CancellationToken.None));

        _ = counts.Should().ContainSingle().Which.Should().Be(1);
        _ = _fakeAckService.InvalidatedPrinterIds.Should().BeEquivalentTo(new[] { acknowledged });
    }
}
