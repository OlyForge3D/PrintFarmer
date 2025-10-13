using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Farm.Web.Api.Tests.Infrastructure;
using Farm.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Tests;

[Trait("Category", "DbHeavy")]
[Collection("DbHeavySerialWithSharedFixture")]
[TestTiming]
public class ExportImportIntegrationTests : CustomDbHeavyTestBase
{
    public ExportImportIntegrationTests() : base(new CustomWebApplicationFactory()) { }

    [Fact]
    public async Task StreamExport_json_streams_objectsAsync()
    {
        var client = _factory.CreateClient();

        // Seed printer directly
        Guid printerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var m = new Manufacturer { Id = Guid.NewGuid(), Name = "JSONMan" };
            db.Manufacturers.Add(m);
            var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = m.Id, Name = "JSONModel" };
            db.Models.Add(model);
            var p = new Printer { Id = Guid.NewGuid(), Name = "json-test", ServerUrl = "http://localhost:7127", ManufacturerId = m.Id, ModelId = model.Id, Backend = (int)PrinterBackend.Moonraker };
            db.Printers.Add(p);
            await db.SaveChangesAsync();
            printerId = p.Id;
        }

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/printers/export/file?format=json")
        {
            Content = JsonContent.Create(new Guid[] { printerId })
        };

        var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        // Read streaming array start and at least one object
        var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task JsonImport_bulk_creates_printersAsync()
    {
        var client = _factory.CreateClient();

        // Build JSON body with two printers
    var create1 = new CreatePrinterDto { Name = "import-json-1", ServerUrl = "http://host1.local:7128", Backend = PrinterBackend.Moonraker, NewManufacturerName = "ImporterMan", NewModelName = "ImporterModel" };
    var create2 = new CreatePrinterDto { Name = "import-json-2", ServerUrl = "http://host2.local:7129", Backend = PrinterBackend.Moonraker, NewManufacturerName = "ImporterMan", NewModelName = "ImporterModel" };
        var dtos = new[] { create1, create2 };

        // Use admin role by default in Test auth handler
        var resp = await client.PostAsJsonAsync("/api/printers/bulk", dtos);
        resp.EnsureSuccessStatusCode();
    var result = await resp.Content.ReadFromJsonAsync<JsonElement>();
    // API uses camelCase JSON naming policy
    result.TryGetProperty("importedCount", out var imported).Should().BeTrue();
    imported.GetInt32().Should().Be(2);
    }
}
