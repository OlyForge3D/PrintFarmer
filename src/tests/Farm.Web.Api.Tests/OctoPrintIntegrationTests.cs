using System.Net;
using System.Text.Json;
using Farm.Web.Shared;
using FluentAssertions;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Farm.Web.Api.Data;
using Farm.Web.Api.Tests.Infrastructure;

namespace Farm.Web.Api.Tests;

public class OctoPrintIntegrationTests : CustomDbHeavyTestBase
{
    public OctoPrintIntegrationTests() : base(new CustomWebApplicationFactory())
    {
    }

    [Fact]
    public async Task Create_OctoPrint_printer_and_get_status()
    {

        // Ensure the test database is created before running the test
        var scopeFactory = _factory.Services.GetService<IServiceScopeFactory>();
        using (var scope = scopeFactory!.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        var client = _factory.CreateClient();
        var createDto = new CreatePrinterDto
        {
            Name = "itest-octoprint",
            ServerUrl = "http://localhost:5000",
            Backend = PrinterBackend.OctoPrint,
            ApiKey = "dummy-key"
        };

        // Retry POST /api/printers if DB is not ready (ServiceUnavailable/Database service unavailable)
        const int maxAttempts = 5;
        int attempt = 0;
        HttpResponseMessage created = null!;
        string errorContent = string.Empty;
        while (attempt < maxAttempts)
        {
            created = await client.PostAsJsonAsync("/api/printers", createDto);
            if (created.IsSuccessStatusCode)
            {
                break;
            }
            errorContent = await created.Content.ReadAsStringAsync();
            if (created.StatusCode == HttpStatusCode.ServiceUnavailable && errorContent.Contains("Database service unavailable"))
            {
                await Task.Delay(500); // Wait 0.5s and retry
                attempt++;
                continue;
            }
            break; // Other errors, don't retry
        }
        if (!created.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException($"POST /api/printers failed: {created.StatusCode}\nResponse: {errorContent}");
        }
        created.IsSuccessStatusCode.Should().BeTrue();
        var dto = await created.Content.ReadFromJsonAsync<PrinterDto>();
        dto.Should().NotBeNull();
        dto!.Name.Should().Be("itest-octoprint");
        dto.Backend.Should().Be(PrinterBackend.OctoPrint);
        // Try to get status (will fail to connect to real OctoPrint, but should not crash)
        var status = await client.GetAsync($"/api/printers/{dto.Id}/status");
        status.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable);
        // Clean up
        var del = await client.DeleteAsync($"/api/printers/{dto.Id}");
        del.IsSuccessStatusCode.Should().BeTrue();
    }
}
