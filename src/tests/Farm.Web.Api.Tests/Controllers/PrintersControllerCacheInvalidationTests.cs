using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Verifies that <see cref="Farm.Modules.Printers.Controllers.PrintersController.UpdateAsync"/> notifies
/// <see cref="IPrinterCacheInvalidator"/> exactly when a printer edit is durably committed
/// (issue #1763). This is the invalidation half of the polling-cache perf fix: the polling
/// services only stop re-querying the printer row per tick because an edit is guaranteed to
/// clear their cached copy immediately, not merely within the 30s reconciliation window.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PrintersControllerCacheInvalidationTests : IAsyncLifetime, IDisposable
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

    public void Dispose()
    {
        _client?.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task UpdatePrinter_SuccessfulSave_InvalidatesCacheForEditedPrinter()
    {
        Guid printerId = await SeedPrinterAsync();

        // Loose, not Strict: the four real backend polling services also Subscribe/Unsubscribe
        // against this singleton during host startup/shutdown - only Invalidate is under test.
        Mock<IPrinterCacheInvalidator> invalidator = new(MockBehavior.Loose);

        await using WebApplicationFactory<Program> host = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPrinterCacheInvalidator>();
                services.AddSingleton(invalidator.Object);
            });
        });
        using HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = _client!.DefaultRequestHeaders.Authorization;

        // #900: UpdateAsync is If-Match protected. Fetch the current ETag from the GET
        // endpoint and include it in the PUT request, otherwise the server returns 428.
        HttpResponseMessage getResponse = await client.GetAsync($"/api/printers/{printerId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string? etag = getResponse.Headers.ETag?.Tag;
        etag.Should().NotBeNullOrEmpty();

        var putRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/printers/{printerId}")
        {
            Content = JsonContent.Create(new UpdatePrinterDto(Name: "renamed-printer"))
        };
        putRequest.Headers.IfMatch.ParseAdd(etag!);
        HttpResponseMessage response = await client.SendAsync(putRequest);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        invalidator.Verify(i => i.Invalidate(printerId), Times.Once);
    }

    [Fact]
    public async Task UpdatePrinter_StaleETag_DoesNotInvalidateCache()
    {
        Guid printerId = await SeedPrinterAsync();
        Mock<IPrinterCacheInvalidator> invalidator = new(MockBehavior.Loose);

        await using WebApplicationFactory<Program> host = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPrinterCacheInvalidator>();
                services.AddSingleton(invalidator.Object);
            });
        });
        using HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = _client!.DefaultRequestHeaders.Authorization;

        // A stale/incorrect If-Match value triggers the 412 concurrency-conflict path, which
        // must not touch the printer row at all, and therefore must not invalidate anything.
        var putRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/printers/{printerId}")
        {
            Content = JsonContent.Create(new UpdatePrinterDto(Name: "renamed-printer"))
        };
        // A syntactically valid but wrong base-64 RowVersion mismatches the printer's actual
        // revision, triggering PrinterRevisionConflict (412) rather than the 400 that an
        // invalid base-64 payload would produce.
        putRequest.Headers.IfMatch.ParseAdd($"\"{Convert.ToBase64String(new byte[8])}\"");
        HttpResponseMessage response = await client.SendAsync(putRequest);

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        invalidator.Verify(i => i.Invalidate(It.IsAny<Guid>()), Times.Never);
    }

    private async Task<Guid> SeedPrinterAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"Cache-Invalidation-Mfr-{suffix}"
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = $"Cache-Invalidation-Model-{suffix}",
            ManufacturerId = manufacturer.Id
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"cache-invalidation-printer-{suffix}",
            ServerUrl = $"http://cache-invalidation-printer-{suffix}.local",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = false
        };

        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.Printers.Add(printer);
        await db.SaveChangesAsync();
        return printer.Id;
    }
}
