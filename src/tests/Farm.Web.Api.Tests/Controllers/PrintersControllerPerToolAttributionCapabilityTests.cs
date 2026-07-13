using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

[Trait("Category", "Integration")]
public sealed class PrintersControllerPerToolAttributionCapabilityTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _client = await _factory.CreateAdminClientAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task UpdatePrinter_BackendChangesAwayFromMoonraker_ClearsCapability()
    {
        Guid printerId = await SeedEligibleMoonrakerPrinterAsync();
        var update = new UpdatePrinterDto(Backend: PrinterBackend.PrusaLink);

        HttpResponseMessage response = await _client!.PutAsJsonAsync(
            $"/api/printers/{printerId}",
            update);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer updated = await db.Printers.SingleAsync(printer => printer.Id == printerId);
        updated.Backend.Should().Be((int)PrinterBackend.PrusaLink);
        updated.SupportsPerToolAttribution.Should().BeFalse();
    }

    private async Task<Guid> SeedEligibleMoonrakerPrinterAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"Capability-Mfr-{suffix}"
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = $"Capability-Model-{suffix}",
            ManufacturerId = manufacturer.Id
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"capability-printer-{suffix}",
            ServerUrl = $"http://capability-printer-{suffix}.local",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            Backend = (int)PrinterBackend.Moonraker,
            IsEnabled = false,
            SupportsPerToolAttribution = true,
            Toolheads =
            [
                PhysicalToolhead(index: 0),
                PhysicalToolhead(index: 1)
            ]
        };

        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.Printers.Add(printer);
        await db.SaveChangesAsync();
        return printer.Id;
    }

    private static Toolhead PhysicalToolhead(int index) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"T{index}",
        Index = index,
        IsPrimary = index == 0,
        ToolheadType = ToolheadType.Physical
    };
}
