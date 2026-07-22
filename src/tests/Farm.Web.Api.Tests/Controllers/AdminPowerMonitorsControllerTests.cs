using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for <see cref="Farm.Web.Api.Controllers.Admin.AdminPowerMonitorsController"/>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class AdminPowerMonitorsControllerTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public AdminPowerMonitorsControllerTests()
    {
        _factory = new CustomWebApplicationFactory(new System.Collections.Generic.Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
        });
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateAdminClientAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory.Dispose();
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private async Task<Printer> SeedPrinterAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Manufacturer? manufacturer = await db.Manufacturers.FirstOrDefaultAsync();
        if (manufacturer is null)
        {
            manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Test Manufacturer" };
            db.Manufacturers.Add(manufacturer);
            await db.SaveChangesAsync();
        }

        PrinterModel? model = await db.PrinterModels.FirstOrDefaultAsync();
        if (model is null)
        {
            model = new PrinterModel { Id = Guid.NewGuid(), Name = "Test Model", ManufacturerId = manufacturer.Id };
            db.PrinterModels.Add(model);
            await db.SaveChangesAsync();
        }

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Test Printer",
            ServerUrl = "http://192.168.1.100",
            BackendPort = 7125,
            Backend = (int)Farm.Infrastructure.Domain.PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };

        db.Printers.Add(printer);
        await db.SaveChangesAsync();
        return printer;
    }

    private async Task<PowerMonitor> SeedPowerMonitorAsync(Printer printer)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pm = new PowerMonitor
        {
            PrinterId = printer.Id,
            ProviderType = "Tasmota",
            DeviceAddress = "192.168.1.10",
            ElectricityRateUsdPerKwh = 0.12m,
            IsEnabled = true,
        };

        db.PowerMonitors.Add(pm);
        await db.SaveChangesAsync();
        return pm;
    }

    // ─── GET /api/admin/power-monitors ──────────────────────────────────────

    [Fact]
    public async Task GetAll_Returns200WithList()
    {
        Printer printer = await SeedPrinterAsync();
        await SeedPowerMonitorAsync(printer);

        HttpResponseMessage resp = await _client.GetAsync("/api/admin/power-monitors");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        List<PowerMonitorDto>? list = await resp.Content.ReadFromJsonAsync<List<PowerMonitorDto>>(JsonOptions);
        list.Should().NotBeNull().And.ContainSingle();
        list![0].PrinterId.Should().Be(printer.Id);
        list[0].Provider.Should().Be("Tasmota");
    }

    [Fact]
    public async Task GetAll_Unauthenticated_Returns401()
    {
        using HttpClient anon = _factory.CreateClient();
        HttpResponseMessage resp = await anon.GetAsync("/api/admin/power-monitors");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ─── GET /api/admin/power-monitors/{id} ─────────────────────────────────

    [Fact]
    public async Task GetById_ExistingId_Returns200()
    {
        Printer printer = await SeedPrinterAsync();
        PowerMonitor pm = await SeedPowerMonitorAsync(printer);

        HttpResponseMessage resp = await _client.GetAsync($"/api/admin/power-monitors/{pm.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        PowerMonitorDto? dto = await resp.Content.ReadFromJsonAsync<PowerMonitorDto>(JsonOptions);
        dto.Should().NotBeNull();
        dto!.Id.Should().Be(pm.Id);
        dto.DeviceAddress.Should().Be("192.168.1.10");
        dto.ElectricityRatePerKwh.Should().Be(0.12m);
        dto.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task GetById_NonExistentId_Returns404()
    {
        HttpResponseMessage resp = await _client.GetAsync("/api/admin/power-monitors/99999");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── POST /api/admin/power-monitors ─────────────────────────────────────

    [Fact]
    public async Task Create_ValidRequest_Returns201WithDto()
    {
        Printer printer = await SeedPrinterAsync();

        var request = new CreatePowerMonitorRequest
        {
            PrinterId = printer.Id,
            Provider = "Tasmota",
            DeviceAddress = "10.0.0.5",
            ElectricityRatePerKwh = 0.25m,
            Enabled = true,
        };

        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/admin/power-monitors", request, JsonOptions);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        PowerMonitorDto? dto = await resp.Content.ReadFromJsonAsync<PowerMonitorDto>(JsonOptions);
        dto.Should().NotBeNull();
        dto!.PrinterId.Should().Be(printer.Id);
        dto.Provider.Should().Be("Tasmota");
        dto.DeviceAddress.Should().Be("10.0.0.5");
        dto.ElectricityRatePerKwh.Should().Be(0.25m);
        dto.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Create_UnknownProvider_Returns400()
    {
        Printer printer = await SeedPrinterAsync();

        var request = new CreatePowerMonitorRequest
        {
            PrinterId = printer.Id,
            Provider = "UnknownProvider",
            DeviceAddress = "10.0.0.5",
        };

        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/admin/power-monitors", request, JsonOptions);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_NonExistentPrinter_Returns400()
    {
        var request = new CreatePowerMonitorRequest
        {
            PrinterId = Guid.NewGuid(),
            Provider = "Tasmota",
            DeviceAddress = "10.0.0.5",
        };

        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/admin/power-monitors", request, JsonOptions);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_EmptyDeviceAddress_Returns400()
    {
        Printer printer = await SeedPrinterAsync();

        var request = new CreatePowerMonitorRequest
        {
            PrinterId = printer.Id,
            Provider = "Tasmota",
            DeviceAddress = "",
        };

        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/admin/power-monitors", request, JsonOptions);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── PUT /api/admin/power-monitors/{id} ─────────────────────────────────

    [Fact]
    public async Task Update_ExistingMonitor_Returns200WithUpdatedDto()
    {
        Printer printer = await SeedPrinterAsync();
        PowerMonitor pm = await SeedPowerMonitorAsync(printer);

        var request = new UpdatePowerMonitorRequest
        {
            PrinterId = printer.Id,
            Provider = "Shelly",
            DeviceAddress = "192.168.2.20",
            ElectricityRatePerKwh = 0.15m,
            Enabled = false,
        };

        HttpResponseMessage resp = await _client.PutAsJsonAsync($"/api/admin/power-monitors/{pm.Id}", request, JsonOptions);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        PowerMonitorDto? dto = await resp.Content.ReadFromJsonAsync<PowerMonitorDto>(JsonOptions);
        dto.Should().NotBeNull();
        dto!.Provider.Should().Be("Shelly");
        dto.DeviceAddress.Should().Be("192.168.2.20");
        dto.ElectricityRatePerKwh.Should().Be(0.15m);
        dto.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Update_NonExistentId_Returns404()
    {
        Printer printer = await SeedPrinterAsync();

        var request = new UpdatePowerMonitorRequest
        {
            PrinterId = printer.Id,
            Provider = "Tasmota",
            DeviceAddress = "10.0.0.1",
        };

        HttpResponseMessage resp = await _client.PutAsJsonAsync("/api/admin/power-monitors/99999", request, JsonOptions);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── DELETE /api/admin/power-monitors/{id} ──────────────────────────────

    [Fact]
    public async Task Delete_ExistingMonitor_Returns204()
    {
        Printer printer = await SeedPrinterAsync();
        PowerMonitor pm = await SeedPowerMonitorAsync(printer);

        HttpResponseMessage resp = await _client.DeleteAsync($"/api/admin/power-monitors/{pm.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify it's gone
        HttpResponseMessage getResp = await _client.GetAsync($"/api/admin/power-monitors/{pm.Id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_NonExistentId_Returns404()
    {
        HttpResponseMessage resp = await _client.DeleteAsync("/api/admin/power-monitors/99999");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── POST /api/admin/power-monitors/test ────────────────────────────────

    [Fact]
    public async Task TestConnection_ValidProvider_ReturnsSuccessResponse()
    {
        var request = new TestPowerMonitorConnectionRequest
        {
            Provider = "Tasmota",
            DeviceAddress = "192.168.1.99",
        };

        // Test connection will fail (no real device), but should return 200 with success=false
        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/admin/power-monitors/test", request, JsonOptions);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        TestPowerMonitorConnectionResponse? result = await resp.Content.ReadFromJsonAsync<TestPowerMonitorConnectionResponse>(JsonOptions);
        result.Should().NotBeNull();
        // success may be true or false depending on network; just verify shape
        result!.Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TestConnection_UnknownProvider_Returns400()
    {
        var request = new TestPowerMonitorConnectionRequest
        {
            Provider = "BogusProvider",
            DeviceAddress = "192.168.1.1",
        };

        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/admin/power-monitors/test", request, JsonOptions);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TestConnection_EmptyDeviceAddress_Returns400()
    {
        var request = new TestPowerMonitorConnectionRequest
        {
            Provider = "Tasmota",
            DeviceAddress = "",
        };

        HttpResponseMessage resp = await _client.PostAsJsonAsync("/api/admin/power-monitors/test", request, JsonOptions);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
