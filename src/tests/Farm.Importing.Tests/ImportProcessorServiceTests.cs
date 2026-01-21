using System;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Importing.Services.Import;
using Farm.Infrastructure;
using Microsoft.EntityFrameworkCore;
using FluentValidation;
using FluentValidation.Results;
using Xunit;

namespace Farm.Importing.Tests;

public class ImportProcessorServiceTests
{
    private static AppDbContext CreateInMemoryDb(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options;
        return new AppDbContext(options);
    }

    // Minimal validator used by tests
    private class DummyCreatePrinterDtoValidator : AbstractValidator<CreatePrinterDto>
    {
        public DummyCreatePrinterDtoValidator() => RuleFor(x => x.Name).NotNull();
    }

    [Fact]
    public async Task ProcessAsync_SkipExisting_PrinterSkipped()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        db.Manufacturers.Add(new Manufacturer { Id = Guid.NewGuid(), Name = "M" });
        var existing = new Printer { Id = Guid.NewGuid(), Name = "P1", ServerUrl = "http://p1" };
        db.Printers.Add(existing);
        await db.SaveChangesAsync();

        // Wrap DbContext in UnitOfWork for service dependency
        using var unitOfWork = new AppUnitOfWork(db);
        var processor = new ImportProcessorService(unitOfWork, null!, new DummyCreatePrinterDtoValidator());

        var dtos = new[] { new CreatePrinterDto { Name = "P1", ServerUrl = "http://p1" } };
        var results = await processor.ProcessAsync(dtos, "skip", default);
        Assert.Single(results);
        Assert.Equal("Skipped", results[0].Status);
    }

    [Fact]
    public async Task ProcessAsync_UpdateExisting_Updates()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var man = new Manufacturer { Id = Guid.NewGuid(), Name = "M" };
        db.Manufacturers.Add(man);
        var existing = new Printer { Id = Guid.NewGuid(), Name = "P1", ServerUrl = "http://p1", Notes = "old" };
        db.Printers.Add(existing);
        await db.SaveChangesAsync();

        // Wrap DbContext in UnitOfWork for service dependency
        using var unitOfWork = new AppUnitOfWork(db);
        var processor = new ImportProcessorService(unitOfWork, null!, new DummyCreatePrinterDtoValidator());
        var dtos = new[] { new CreatePrinterDto { Name = "P1", ServerUrl = "http://p1", Notes = "new" } };
        var results = await processor.ProcessAsync(dtos, "update", default);
        Assert.Single(results);
        Assert.Equal("Imported", results[0].Status);

        var updated = await db.Printers.FindAsync(existing.Id);
        Assert.Equal("new", updated!.Notes);
    }
}
