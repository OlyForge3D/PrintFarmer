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
/// Integration tests verifying PUT /api/printers/{id} backward compatibility for issue #1617's
/// Printer.HasHeatedChamber -&gt; Printer.CalibrationHasHeatedChamber rename: the deprecated
/// UpdatePrinterDto.HasHeatedChamber alias must still be accepted during the deprecation window,
/// the new UpdatePrinterDto.CalibrationHasHeatedChamber field must work standalone, and when both
/// are supplied on the same request the new field must take precedence.
/// </summary>
[Trait("Category", "Integration")]
public class PrintersControllerCalibrationHasHeatedChamberBackCompatTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;

    public PrintersControllerCalibrationHasHeatedChamberBackCompatTests()
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
            CalibrationHasHeatedChamber = null,
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        return printer.Id;
    }

    [Fact]
    public async Task UpdatePrinter_WhenLegacyHasHeatedChamberFieldSupplied_PersistsToCalibrationHasHeatedChamber()
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(HasHeatedChamber: true);

        HttpResponseMessage response = await PutPrinterAsync(id, dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        bool? persisted = await GetPersistedCalibrationHasHeatedChamberAsync(id);
        persisted.Should().BeTrue(
            because: "the deprecated legacy field must still be accepted during the deprecation window (issue #1617)");
    }

    [Fact]
    public async Task UpdatePrinter_WhenCanonicalCalibrationHasHeatedChamberFieldSupplied_PersistsToCalibrationHasHeatedChamber()
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(CalibrationHasHeatedChamber: true);

        HttpResponseMessage response = await PutPrinterAsync(id, dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        bool? persisted = await GetPersistedCalibrationHasHeatedChamberAsync(id);
        persisted.Should().BeTrue(because: "the new canonical field must be accepted and persisted");
    }

    [Fact]
    public async Task UpdatePrinter_WhenBothHasHeatedChamberFieldsSupplied_CanonicalFieldTakesPrecedence()
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(
            CalibrationHasHeatedChamber: true,
            HasHeatedChamber: false);

        HttpResponseMessage response = await PutPrinterAsync(id, dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        bool? persisted = await GetPersistedCalibrationHasHeatedChamberAsync(id);
        persisted.Should().BeTrue(
            because: "CalibrationHasHeatedChamber must win over the deprecated legacy HasHeatedChamber alias when both are supplied");
    }

    // Reviewer note (Hicks): the fact above alone (true/false -> true) would also pass under an
    // incorrect OR-like implementation (either field true wins). This inverse case (false/true ->
    // false) is the one that actually proves ?? null-coalescing precedence rather than an OR.
    [Fact]
    public async Task UpdatePrinter_WhenBothHasHeatedChamberFieldsSupplied_CanonicalFalseWinsOverLegacyTrue()
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(
            CalibrationHasHeatedChamber: false,
            HasHeatedChamber: true);

        HttpResponseMessage response = await PutPrinterAsync(id, dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        bool? persisted = await GetPersistedCalibrationHasHeatedChamberAsync(id);
        persisted.Should().BeFalse(
            because: "CalibrationHasHeatedChamber must win via ?? precedence even when it is explicitly false and the legacy alias is true");
    }

    private async Task<bool?> GetPersistedCalibrationHasHeatedChamberAsync(Guid printerId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer printer = await db.Printers.SingleAsync(p => p.Id == printerId);
        return printer.CalibrationHasHeatedChamber;
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
