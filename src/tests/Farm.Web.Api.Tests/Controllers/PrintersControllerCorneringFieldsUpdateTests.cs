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
/// Integration test verifying <c>PUT /api/printers/{id}</c> persists the three new cornering
/// calibration fields (issue #2138) — <see cref="Printer.MaxJerk"/>,
/// <see cref="Printer.JunctionDeviation"/>, <see cref="Printer.SquareCornerVelocity"/> — through
/// <see cref="Farm.Modules.Calibration.Services.Calibration.CalibrationPrinterUpdateMapper.ApplyPrinter"/>,
/// mirroring the existing <see cref="Printer.MaxAcceleration"/> field. This also exercises the
/// <c>AddPrinterCorneringFields</c> EF Core migration end-to-end against a real (SQLite)
/// <see cref="AppDbContext"/>, so migration drift on these columns is caught by a test run rather
/// than at deploy time.
/// </summary>
/// <remarks>
/// Per Dallas's architecture decision on #2138, this write is a distinct, explicit, separate
/// admin action — the same admin-gated endpoint used for every other printer field, never
/// automatically written by the (report-only) cornering calibration pipeline itself. No
/// calibration-flow code path exercises this endpoint; only an explicit admin PUT does.
/// </remarks>
[Trait("Category", "Integration")]
public class PrintersControllerCorneringFieldsUpdateTests : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;

    public PrintersControllerCorneringFieldsUpdateTests(CustomWebApplicationFactory factory)
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
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        return printer.Id;
    }

    [Fact]
    public async Task UpdatePrinter_WhenCorneringFieldsSupplied_PersistsAllThree()
    {
        Guid printerId = await SeedPrinterAsync();

        var dto = new UpdatePrinterDto(
            MaxJerk: 10,
            JunctionDeviation: 0.013,
            SquareCornerVelocity: 5.0);

        HttpResponseMessage response = await PutPrinterAsync(printerId, dto);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer persisted = await db.Printers.SingleAsync(p => p.Id == printerId);

        persisted.MaxJerk.Should().Be(10);
        persisted.JunctionDeviation.Should().Be(0.013);
        persisted.SquareCornerVelocity.Should().Be(5.0);
    }

    [Fact]
    public async Task UpdatePrinter_WhenCorneringFieldsOmitted_LeavesExistingValuesUnchanged()
    {
        // Mirrors the null-means-"don't touch" semantics of MaxAcceleration/MaxTravelAcceleration:
        // an update that doesn't mention these fields must not clear them.
        Guid printerId = await SeedPrinterAsync();
        HttpResponseMessage seedResponse = await PutPrinterAsync(
            printerId,
            new UpdatePrinterDto(MaxJerk: 8, JunctionDeviation: 0.02, SquareCornerVelocity: 4.5));
        seedResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage response = await PutPrinterAsync(
            printerId,
            new UpdatePrinterDto(Notes: "unrelated update"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer persisted = await db.Printers.SingleAsync(p => p.Id == printerId);

        persisted.MaxJerk.Should().Be(8);
        persisted.JunctionDeviation.Should().Be(0.02);
        persisted.SquareCornerVelocity.Should().Be(4.5);
        persisted.Notes.Should().Be("unrelated update");
    }

    [Theory]
    [InlineData(-1, null, null, "maxJerk")]
    [InlineData(null, -0.01, null, "junctionDeviation")]
    [InlineData(null, null, -1.0, "squareCornerVelocity")]
    public async Task UpdatePrinter_WhenCorneringFieldIsNegative_ReturnsBadRequestAndDoesNotPersist(
        int? maxJerk, double? junctionDeviation, double? squareCornerVelocity, string expectedField)
    {
        // Vasquez review (issue #2138): a negative motion-planner tunable is physically
        // nonsensical and must be rejected with 400 rather than silently persisted -- it could
        // later flow into slicer/firmware configuration downstream.
        Guid printerId = await SeedPrinterAsync();

        var dto = new UpdatePrinterDto(
            MaxJerk: maxJerk,
            JunctionDeviation: junctionDeviation,
            SquareCornerVelocity: squareCornerVelocity);

        HttpResponseMessage response = await PutPrinterAsync(printerId, dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(expectedField);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer persisted = await db.Printers.SingleAsync(p => p.Id == printerId);

        persisted.MaxJerk.Should().BeNull();
        persisted.JunctionDeviation.Should().BeNull();
        persisted.SquareCornerVelocity.Should().BeNull();
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
