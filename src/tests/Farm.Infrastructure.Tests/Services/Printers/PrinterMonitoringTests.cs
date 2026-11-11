using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

public sealed class PrinterMonitoringTests
{
    [Theory]
    [InlineData(PrintJobStatus.Assigned)]
    [InlineData(PrintJobStatus.Starting)]
    [InlineData(PrintJobStatus.Printing)]
    [InlineData(PrintJobStatus.Paused)]
    public async Task HasActiveWorkAsync_ReturnsTrueForAssignedOrActiveJobs(PrintJobStatus status)
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = new() { Name = "Synthetic", ServerUrl = "http://synthetic.invalid" };
        db.Printers.Add(printer);
        db.PrintJobs.Add(new PrintJob
        {
            Name = "Active work",
            AssignedPrinterId = printer.Id,
            Status = status,
        });
        await db.SaveChangesAsync();
        PrinterMonitoringService monitoring = new(db, Mock.Of<IPrinterStatusCacheReader>());

        (await monitoring.HasActiveWorkAsync(printer.Id, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task HasActiveWorkAsync_UsesCachedEvidenceWhenNoPersistedJobExists()
    {
        await using AppDbContext db = CreateDbContext();
        Guid printerId = Guid.NewGuid();
        Mock<IPrinterStatusCacheReader> statusCache = new();
        statusCache.Setup(cache => cache.GetSnapshot(printerId))
            .Returns(new PrinterStatusCacheSnapshot(
                new PrinterStatusDto(printerId, false, "Offline"),
                DateTime.UtcNow,
                HasActivePrintAtDisconnect: true));
        PrinterMonitoringService monitoring = new(db, statusCache.Object);

        (await monitoring.HasActiveWorkAsync(printerId, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task GetReviewRequiredJobsAsync_ReturnsOnlyMarkedJobsThatStillOccupyPrinters()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = new()
        {
            Name = "Out of service",
            ServerUrl = "http://synthetic.invalid",
            IsEnabled = false,
        };
        db.Printers.Add(printer);
        db.PrintJobs.AddRange(
            new PrintJob
            {
                Name = "Assigned review",
                AssignedPrinterId = printer.Id,
                Status = PrintJobStatus.Assigned,
                RequiresOperatorReview = true,
            },
            new PrintJob
            {
                Name = "Paused review",
                AssignedPrinterId = printer.Id,
                Status = PrintJobStatus.Paused,
                RequiresOperatorReview = true,
            },
            new PrintJob
            {
                Name = "Resolved review",
                AssignedPrinterId = printer.Id,
                Status = PrintJobStatus.Completed,
                RequiresOperatorReview = true,
            },
            new PrintJob
            {
                Name = "Unmarked active",
                AssignedPrinterId = printer.Id,
                Status = PrintJobStatus.Printing,
            });
        await db.SaveChangesAsync();
        PrinterMonitoringService monitoring = new(db, Mock.Of<IPrinterStatusCacheReader>());

        IReadOnlyList<PrinterReviewRequiredJob> result =
            await monitoring.GetReviewRequiredJobsAsync(CancellationToken.None);

        result.Select(job => job.JobName).Should().BeEquivalentTo(["Assigned review", "Paused review"]);
        result.Should().OnlyContain(job => job.PrinterId == printer.Id && job.PrinterName == "Out of service");
    }

    private static AppDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
