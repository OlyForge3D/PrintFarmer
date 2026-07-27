// <copyright file="PrinterMutationEtagTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class PrinterMutationEtagTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private Guid _printerId;

    public async Task InitializeAsync()
    {
        _client = await _factory.CreateAdminClientAsync();
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "ETag maker" };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = "ETag model",
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "ETag printer",
            ServerUrl = $"http://etag-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            IsEnabled = true,
            IsAvailable = true,
        };
        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.Printers.Add(printer);
        db.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printer.Id });
        await db.SaveChangesAsync();
        _printerId = printer.Id;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task MaintenanceMutation_MissingIfMatch_Returns428()
    {
        HttpResponseMessage response = await _client.PutAsJsonAsync(
            $"/api/printers/{_printerId}/maintenance",
            true);

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
    }

    [Fact]
    public async Task MaintenanceMutation_TwoWritersSameEtag_SecondReturns412()
    {
        HttpResponseMessage current = await _client.GetAsync(
            $"/api/printers/{_printerId}");
        current.EnsureSuccessStatusCode();
        string currentBody = await current.Content.ReadAsStringAsync();
        string etag = current.Headers.ETag?.Tag
            ?? throw new InvalidOperationException(
                $"Printer GET did not return an ETag. Body: {currentBody}");

        HttpResponseMessage first = await PutMaintenanceAsync(true, etag);
        HttpResponseMessage second = await PutMaintenanceAsync(false, etag);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Printers.FindAsync(_printerId))!.InMaintenance.Should().BeTrue();
    }

    private async Task<HttpResponseMessage> PutMaintenanceAsync(
        bool enabled,
        string etag)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/printers/{_printerId}/maintenance")
        {
            Content = JsonContent.Create(enabled),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await _client.SendAsync(request);
    }
}
