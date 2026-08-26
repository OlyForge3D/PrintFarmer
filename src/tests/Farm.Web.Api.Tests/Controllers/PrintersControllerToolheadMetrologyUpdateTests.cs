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
/// Integration test verifying <c>PUT /api/printers/{id}</c> still applies the
/// per-toolhead manual metrology fields (offsets, drive type, isDirectDrive,
/// extruder gear ratio, max volumetric flow, nozzle material/hardness) via
/// <see cref="Services.Calibration.CalibrationPrinterUpdateMapper.ApplyToolhead"/>.
/// This regression guards against re-breaking the shared mapper while removing the
/// dedicated <c>PUT /api/printers/{id}/calibration-setup</c> endpoint (issue #1942):
/// that endpoint was the only other caller of <c>ApplyToolhead</c>, and the general
/// update path (<c>PrintersController.cs</c>) must keep working unchanged.
/// </summary>
[Trait("Category", "Integration")]
public class PrintersControllerToolheadMetrologyUpdateTests : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;

    public PrintersControllerToolheadMetrologyUpdateTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
        _client = await _factory.CreateAdminClientAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        return Task.CompletedTask;
    }

    private async Task<(Guid PrinterId, Guid ToolheadId)> SeedPrinterWithToolheadAsync()
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
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        var toolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Index = 0,
            Name = "Tool 0",
            IsPrimary = true,
        };
        db.Toolheads.Add(toolhead);
        await db.SaveChangesAsync();

        return (printer.Id, toolhead.Id);
    }

    [Fact]
    public async Task UpdatePrinter_WhenToolheadMetrologySupplied_PersistsViaApplyToolhead()
    {
        (Guid printerId, Guid toolheadId) = await SeedPrinterWithToolheadAsync();

        var toolheadUpdate = new UpdateToolheadDto(
            Id: toolheadId,
            OffsetX: 12.5,
            OffsetY: -3.25,
            OffsetZ: 0.4,
            NozzleMaterial: "HardenedSteel",
            NozzleIsHardened: true,
            MaxVolumetricFlow: 15.0,
            DriveType: "BowdenExtruder",
            IsDirectDrive: false,
            ExtruderGearRatio: "3:1");
        var dto = new UpdatePrinterDto(Toolheads: [toolheadUpdate]);

        HttpResponseMessage response = await PutPrinterAsync(printerId, dto);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Toolhead persisted = await db.Toolheads.SingleAsync(t => t.Id == toolheadId);

        persisted.OffsetX.Should().Be(12.5);
        persisted.OffsetY.Should().Be(-3.25);
        persisted.OffsetZ.Should().Be(0.4);
        persisted.NozzleMaterial.Should().Be("HardenedSteel");
        persisted.NozzleIsHardened.Should().BeTrue();
        persisted.MaxVolumetricFlow.Should().Be(15.0);
        persisted.DriveType.Should().Be("BowdenExtruder");
        persisted.IsDirectDrive.Should().BeFalse();
        persisted.ExtruderGearRatio.Should().Be("3:1");
    }

    [Theory]
    [InlineData(200.0, null, null, null, "offsetX")]
    [InlineData(null, null, null, 300.0, "maxVolumetricFlow")]
    [InlineData(null, null, null, null, "extruderGearRatio")]
    public async Task UpdatePrinter_WhenToolheadMetrologyOutOfBounds_RejectsWithoutMutating(
        double? invalidOffsetX,
        double? invalidOffsetY,
        double? invalidOffsetZ,
        double? invalidMaxVolumetricFlow,
        string expectedField)
    {
        (Guid printerId, Guid toolheadId) = await SeedPrinterWithToolheadAsync();

        var toolheadUpdate = new UpdateToolheadDto(
            Id: toolheadId,
            OffsetX: invalidOffsetX,
            OffsetY: invalidOffsetY,
            OffsetZ: invalidOffsetZ,
            MaxVolumetricFlow: invalidMaxVolumetricFlow,
            ExtruderGearRatio: expectedField == "extruderGearRatio" ? "not-a-ratio" : null);
        var dto = new UpdatePrinterDto(Toolheads: [toolheadUpdate]);

        HttpResponseMessage response = await PutPrinterAsync(printerId, dto);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("invalid_toolhead_metrology");
        body.Should().Contain(expectedField);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Toolhead persisted = await db.Toolheads.SingleAsync(t => t.Id == toolheadId);

        persisted.OffsetX.Should().BeNull();
        persisted.MaxVolumetricFlow.Should().BeNull();
        persisted.ExtruderGearRatio.Should().BeNull();
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
