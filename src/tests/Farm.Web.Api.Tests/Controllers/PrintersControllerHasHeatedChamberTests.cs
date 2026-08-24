using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration test verifying PUT /api/printers/{id} persists the canonical
/// UpdatePrinterDto.HasHeatedChamber field. Issue #1947 rolled back issue
/// #1617's Printer.HasHeatedChamber -&gt; Printer.CalibrationHasHeatedChamber
/// rename, because the field is read by DispatchSafetyGates for general
/// dispatch-safety, not calibration-specific logic.
/// </summary>
[Trait("Category", "Integration")]
public class PrintersControllerHasHeatedChamberTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;

    public PrintersControllerHasHeatedChamberTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        _client = await _factory.CreateAdminClientAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<Guid> SeedPrinterAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        string suffix = Guid.NewGuid().ToString("N")[..8];

        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"Mfr-{suffix}" };
        db.Manufacturers.Add(manufacturer);
        await db.SaveChangesAsync();

        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = $"Model-{suffix}",
            ManufacturerId = manufacturer.Id,
        };
        db.PrinterModels.Add(model);
        await db.SaveChangesAsync();

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"printer-{suffix}",
            ServerUrl = $"http://printer-{suffix}.local",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            HasHeatedChamber = null,
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        return printer.Id;
    }

    [Fact]
    public async Task UpdatePrinter_WhenHasHeatedChamberFieldSupplied_PersistsToHasHeatedChamber()
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(HasHeatedChamber: true);

        HttpResponseMessage response = await PutPrinterAsync(id, dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        bool? persisted = await GetPersistedHasHeatedChamberAsync(id);
        persisted.Should().BeTrue(because: "the general dispatch-safety field must be accepted and persisted");
    }

    private async Task<bool?> GetPersistedHasHeatedChamberAsync(Guid printerId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer printer = await db.Printers.SingleAsync(p => p.Id == printerId);
        return printer.HasHeatedChamber;
    }

    private async Task<HttpResponseMessage> PutPrinterAsync(
        Guid printerId,
        UpdatePrinterDto dto)
    {
        HttpResponseMessage current = await _client!.GetAsync(
            $"/api/printers/{printerId}");
        current.EnsureSuccessStatusCode();
        string etag = current.Headers.ETag?.Tag
            ?? throw new InvalidOperationException("Printer GET did not return an ETag.");
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/printers/{printerId}")
        {
            Content = JsonContent.Create(dto),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await _client.SendAsync(request);
    }
}
