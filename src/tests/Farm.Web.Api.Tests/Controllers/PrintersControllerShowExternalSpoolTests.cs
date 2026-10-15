using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
/// Integration tests for the per-printer <see cref="Printer.ShowExternalSpool"/> presentation
/// setting (the QidiBox external rack/feed slot). The wire field is <c>showExternalSpool</c>, it
/// defaults to <c>false</c>, an update that omits it leaves the stored value unchanged, and
/// toggling it never alters any spool assignment or the calibration configuration revision.
/// </summary>
[Trait("Category", "Integration")]
public class PrintersControllerShowExternalSpoolTests : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    private const string WireField = "showExternalSpool";

    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;

    public PrintersControllerShowExternalSpoolTests(CustomWebApplicationFactory factory)
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

    [Fact]
    public async Task ReadEndpoints_WhenNeverSet_ReportShowExternalSpoolFalse()
    {
        Guid printerId = await SeedPrinterAsync();

        (await ReadPrinterFieldAsync($"/api/printers/{printerId}")).Should().BeFalse();
        (await ReadPrinterFieldAsync($"/api/printers/{printerId}/details")).Should().BeFalse();
        (await ReadListFieldAsync(printerId)).Should().BeFalse();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Printers.SingleAsync(p => p.Id == printerId)).ShowExternalSpool.Should().BeFalse();
    }

    [Fact]
    public async Task UpdatePrinter_WhenShowExternalSpoolTrue_PersistsAndRoundTripsOnEveryReadDto()
    {
        Guid printerId = await SeedPrinterAsync();

        HttpResponseMessage response = await PutPrinterAsync(
            printerId,
            new UpdatePrinterDto(ShowExternalSpool: true));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using (JsonDocument putBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            putBody.RootElement.GetProperty(WireField).GetBoolean().Should().BeTrue();
        }

        (await ReadPrinterFieldAsync($"/api/printers/{printerId}")).Should().BeTrue();
        (await ReadPrinterFieldAsync($"/api/printers/{printerId}/details")).Should().BeTrue();
        (await ReadListFieldAsync(printerId)).Should().BeTrue();

        HttpResponseMessage hide = await PutPrinterAsync(
            printerId,
            new UpdatePrinterDto(ShowExternalSpool: false));
        hide.StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReadPrinterFieldAsync($"/api/printers/{printerId}/details")).Should().BeFalse();
    }

    [Fact]
    public async Task UpdatePrinter_WhenShowExternalSpoolOmitted_LeavesExistingValueUnchanged()
    {
        Guid printerId = await SeedPrinterAsync();
        (await PutPrinterAsync(printerId, new UpdatePrinterDto(ShowExternalSpool: true)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage response = await PutPrinterAsync(
            printerId,
            new UpdatePrinterDto(Notes: "unrelated update"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer persisted = await db.Printers.SingleAsync(p => p.Id == printerId);
        persisted.ShowExternalSpool.Should().BeTrue();
        persisted.Notes.Should().Be("unrelated update");
    }

    [Fact]
    public async Task UpdatePrinter_WhenTogglingShowExternalSpool_DoesNotAlterSpoolAssignmentsOrConfigurationRevision()
    {
        Guid printerId = await SeedPrinterAsync(withSpoolAssignments: true);
        Snapshot before = await CaptureAsync(printerId);

        (await PutPrinterAsync(printerId, new UpdatePrinterDto(ShowExternalSpool: true)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        Snapshot afterShow = await CaptureAsync(printerId);

        (await PutPrinterAsync(printerId, new UpdatePrinterDto(ShowExternalSpool: false)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        Snapshot afterHide = await CaptureAsync(printerId);

        afterShow.Should().BeEquivalentTo(before);
        afterHide.Should().BeEquivalentTo(before);
    }

    private sealed record Snapshot(
        int? PrinterSpoolId,
        string? PrinterMaterial,
        long ConfigurationRevision,
        (int Index, int? SpoolId, string? Material, string? Color)[] Toolheads,
        (Guid Id, bool InUse, Guid? AssignedPrinterId, long Revision)[] Spools);

    private async Task<Snapshot> CaptureAsync(Guid printerId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer printer = await db.Printers.AsNoTracking().SingleAsync(p => p.Id == printerId);
        var toolheads = (await db.Toolheads.AsNoTracking()
                .Where(t => t.PrinterId == printerId)
                .OrderBy(t => t.Index)
                .ToListAsync())
            .Select(t => (t.Index, t.CurrentSpoolId, t.CurrentMaterial, t.CurrentFilamentColor))
            .ToArray();
        var spools = (await db.Spools.AsNoTracking().OrderBy(s => s.Id).ToListAsync())
            .Select(s => (s.Id, s.InUse, s.AssignedPrinterId, s.Revision))
            .ToArray();
        toolheads.Should().NotBeEmpty();
        spools.Should().NotBeEmpty();
        return new Snapshot(
            printer.CurrentSpoolId,
            printer.CurrentMaterial,
            printer.ConfigurationRevision,
            toolheads,
            spools);
    }

    private async Task<Guid> SeedPrinterAsync(bool withSpoolAssignments = false)
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
        if (withSpoolAssignments)
        {
            printer.CurrentSpoolId = 41;
            printer.CurrentMaterial = "PLA";
        }

        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        if (withSpoolAssignments)
        {
            for (int gate = 0; gate < 5; gate++)
            {
                db.Toolheads.Add(new Toolhead
                {
                    Id = Guid.NewGuid(),
                    PrinterId = printer.Id,
                    Name = $"Gate {gate}",
                    Index = gate,
                    IsPrimary = gate == 0,
                    CurrentSpoolId = 40 + gate,
                    CurrentMaterial = gate == 4 ? "PETG" : "PLA",
                    CurrentFilamentColor = $"#00000{gate}",
                });
            }

            db.Spools.Add(new Spool
            {
                Id = Guid.NewGuid(),
                Material = "PETG",
                ColorHex = "#000004",
                WeightGrams = 900,
                InUse = true,
                AssignedPrinterId = printer.Id,
            });
            await db.SaveChangesAsync();
        }

        return printer.Id;
    }

    private async Task<bool> ReadPrinterFieldAsync(string url)
    {
        HttpResponseMessage response = await _client!.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty(WireField).GetBoolean();
    }

    private async Task<bool> ReadListFieldAsync(Guid printerId)
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/printers?includeDisabled=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement printer = body.RootElement.EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == printerId);
        return printer.GetProperty(WireField).GetBoolean();
    }

    private async Task<HttpResponseMessage> PutPrinterAsync(Guid printerId, UpdatePrinterDto dto)
    {
        HttpResponseMessage current = await _client!.GetAsync($"/api/printers/{printerId}");
        current.EnsureSuccessStatusCode();
        string etag = current.Headers.ETag?.Tag
            ?? throw new InvalidOperationException("Printer GET did not return an ETag.");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/printers/{printerId}")
        {
            Content = JsonContent.Create(dto),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await _client.SendAsync(request);
    }
}
