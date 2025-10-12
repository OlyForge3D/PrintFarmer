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
public class ExportPrintersIntegrationTests : CustomDbHeavyTestBase
{
    public ExportPrintersIntegrationTests() : base(new CustomWebApplicationFactory())
    {
    }

    [Fact]
    public async Task ExportPrintersByIds_returns_printer_with_capabilitiesAsync()
    {
        var client = _factory.CreateClient();

        // Seed a printer and capabilities directly via scoped DB context
        Guid printerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var m = new Manufacturer { Id = Guid.NewGuid(), Name = "TestMan" };
            db.Manufacturers.Add(m);
            var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = m.Id, Name = "TestModel" };
            db.Models.Add(model);
            var p = new Printer { Id = Guid.NewGuid(), Name = "exp-test", ServerUrl = "http://localhost:7125", ManufacturerId = m.Id, ModelId = model.Id, Backend = (int)PrinterBackend.Moonraker };
            db.Printers.Add(p);
            var cap = new PrinterCapabilities { Id = Guid.NewGuid(), PrinterId = p.Id, IsAvailable = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.PrinterCapabilities.Add(cap);
            await db.SaveChangesAsync();
            printerId = p.Id;
        }

        // Call the export endpoint by IDs
        var resp = await client.PostAsJsonAsync("/api/printers/export", new Guid[] { printerId });
        resp.EnsureSuccessStatusCode();
    var arr = await resp.Content.ReadFromJsonAsync<PrinterWithCapabilitiesDto[]>() ?? Array.Empty<PrinterWithCapabilitiesDto>();
    arr.Length.Should().BeGreaterThan(0);
    var item = arr.FirstOrDefault(a => a.PrinterId == printerId);
    item.Should().NotBeNull();
    item!.Capabilities.Should().NotBeNull();
        item.PrinterName.Should().Be("exp-test");
    }

    [Fact]
    public async Task StreamExport_csv_returns_stream_with_headerAsync()
    {
        var client = _factory.CreateClient();
        // Seed a printer directly in the shared test DB to avoid relying on
        // DefaultCatalogService (which requires full DB seed). This keeps the
        // test hermetic and fast.
        Guid printerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var m = new Manufacturer { Id = Guid.NewGuid(), Name = "StreamMan" };
            db.Manufacturers.Add(m);
            var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = m.Id, Name = "StreamModel" };
            db.Models.Add(model);
            var p = new Printer { Id = Guid.NewGuid(), Name = "stream-test", ServerUrl = "http://localhost:7126", ManufacturerId = m.Id, ModelId = model.Id, Backend = (int)PrinterBackend.Moonraker };
            db.Printers.Add(p);
            await db.SaveChangesAsync();
            printerId = p.Id;
        }

        // Request CSV stream
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/printers/export/file?format=csv")
        {
            Content = JsonContent.Create(new Guid[] { printerId })
        };

        var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");

        var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        string header = await reader.ReadLineAsync();
        header.Should().Contain("Name,ServerUrl,OriginalServerUrl,Notes,ManufacturerName,ModelName,Backend,ApiKey,DateAcquired");
        string firstData = await reader.ReadLineAsync();
        firstData.Should().Contain("stream-test");
    }

    [Fact]
    public async Task ExportEndpoints_require_admin_policy_forbidden_for_regular_userAsync()
    {
        var client = _factory.CreateClient();

        // Register a regular user and get token
        var register = new RegisterRequest("regularuser", "regular@example.com", "TestPassword123!", "Reg", "User");
        var regResp = await client.PostAsJsonAsync("/api/auth/register", register);
        regResp.EnsureSuccessStatusCode();
    var auth = await regResp.Content.ReadFromJsonAsync<AuthenticationResult>() ?? new AuthenticationResult(false, null, null, null, "NoAuth");
    auth.Should().NotBeNull();

    // Use JWT token (non-admin) for request
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);

    // Also instruct the Test auth handler to drop farm_admin role via header
    client.DefaultRequestHeaders.Remove("X-Test-Roles");
    client.DefaultRequestHeaders.Add("X-Test-Roles", "user");

    var resp = await client.PostAsJsonAsync("/api/printers/export", Array.Empty<Guid>());
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

    var resp2 = await client.PostAsync("/api/printers/export/file?format=json", JsonContent.Create(Array.Empty<Guid>()));
        resp2.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
