using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class SpoolmanBarcodeEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Mock<ISpoolmanService> spoolmanServiceMock = new();
    private readonly Mock<IBarcodeScanLogService> barcodeScanLogServiceMock = new();
    private readonly SpoolmanController controller;

    public SpoolmanBarcodeEndpointTests()
    {
        Mock<ISettingsService> settingsServiceMock = new();
        Mock<ILogger<SpoolmanController>> loggerMock = new();
        barcodeScanLogServiceMock
            .Setup(s => s.LogAsync(It.IsAny<BarcodeScanLog>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        controller = new SpoolmanController(spoolmanServiceMock.Object, settingsServiceMock.Object, barcodeScanLogServiceMock.Object, loggerMock.Object);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_KnownArticleNumber_ReturnsOkWithFilament()
    {
        SpoolmanFilamentDto filament = CreateFilament(42, "012345678905");
        spoolmanServiceMock
            .Setup(s => s.GetFilamentByBarcodeAsync("012345678905", It.IsAny<CancellationToken>()))
            .ReturnsAsync(filament);

        ActionResult<SpoolmanFilamentDto> result = await controller.GetFilamentByBarcodeAsync("012345678905", CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        SpoolmanFilamentDto value = Assert.IsType<SpoolmanFilamentDto>(ok.Value);
        Assert.Equal(42, value.Id);
        Assert.Equal("012345678905", value.ArticleNumber);
        VerifyLogged(BarcodeScanAction.Resolve, BarcodeScanOutcome.Resolved, 200, matchedFilamentId: 42);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_UnknownArticleNumber_ReturnsNotFound()
    {
        spoolmanServiceMock
            .Setup(s => s.GetFilamentByBarcodeAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpoolmanFilamentDto?)null);

        ActionResult<SpoolmanFilamentDto> result = await controller.GetFilamentByBarcodeAsync("missing", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        VerifyLogged(BarcodeScanAction.Resolve, BarcodeScanOutcome.NotFound, 404);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_EmptyCode_ReturnsBadRequest()
    {
        ActionResult<SpoolmanFilamentDto> result = await controller.GetFilamentByBarcodeAsync("   ", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_ServiceThrows_LogsErrorOutcome()
    {
        spoolmanServiceMock
            .Setup(s => s.GetFilamentByBarcodeAsync("ERR123", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Spoolman failed"));

        ActionResult<SpoolmanFilamentDto> result = await controller.GetFilamentByBarcodeAsync("ERR123", CancellationToken.None);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, objectResult.StatusCode);
        VerifyLogged(BarcodeScanAction.Resolve, BarcodeScanOutcome.Error, 500);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_QueryCodeWithSlashPercentAndSpace_ReturnsOkWithFilament()
    {
        const string barcode = "ABC/DEF 12%3";
        SpoolmanFilamentDto filament = CreateFilament(44, barcode);
        Mock<ISpoolmanService> routedSpoolmanServiceMock = new();
        routedSpoolmanServiceMock
            .Setup(s => s.GetFilamentByBarcodeAsync(barcode, It.IsAny<CancellationToken>()))
            .ReturnsAsync(filament);

        await using WebApplicationFactory<Program> factory = CustomWebApplicationFactory
            .CreateWithIsolatedDatabase()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ISpoolmanService>();
                    services.AddSingleton(routedSpoolmanServiceMock.Object);
                });
            });
        using HttpClient client = await CreateAuthenticatedClientAsync(factory);

        HttpResponseMessage response = await client.GetAsync($"/api/spoolman/filaments/by-barcode?code={Uri.EscapeDataString(barcode)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SpoolmanFilamentDto? value = await response.Content.ReadFromJsonAsync<SpoolmanFilamentDto>();
        Assert.NotNull(value);
        Assert.Equal(44, value.Id);
        Assert.Equal(barcode, value.ArticleNumber);
        routedSpoolmanServiceMock.Verify(
            s => s.GetFilamentByBarcodeAsync(barcode, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SaveBarcodeMappingAsync_ValidRequest_ReturnsUpdatedFilament()
    {
        SpoolmanFilamentDto filament = CreateFilament(7, "ABC123");
        SpoolmanBarcodeMappingRequest request = new() { Barcode = "ABC123", FilamentId = 7 };
        spoolmanServiceMock
            .Setup(s => s.SaveBarcodeMappingAsync(7, "ABC123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(filament);

        ActionResult<SpoolmanFilamentDto> result = await controller.SaveBarcodeMappingAsync(request, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        SpoolmanFilamentDto value = Assert.IsType<SpoolmanFilamentDto>(ok.Value);
        Assert.Equal(7, value.Id);
        Assert.Equal("ABC123", value.ArticleNumber);
        VerifyLogged(BarcodeScanAction.Mapping, BarcodeScanOutcome.Mapped, 200, matchedFilamentId: 7);
    }

    [Fact]
    public async Task SaveBarcodeMappingAsync_MissingFilament_ReturnsNotFound()
    {
        SpoolmanBarcodeMappingRequest request = new() { Barcode = "ABC123", FilamentId = 404 };
        spoolmanServiceMock
            .Setup(s => s.SaveBarcodeMappingAsync(404, "ABC123", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpoolmanFilamentDto?)null);

        ActionResult<SpoolmanFilamentDto> result = await controller.SaveBarcodeMappingAsync(request, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        VerifyLogged(BarcodeScanAction.Mapping, BarcodeScanOutcome.NotFound, 404, matchedFilamentId: 404);
    }

    [Fact]
    public async Task SaveBarcodeMappingAsync_EmptyBarcode_ReturnsBadRequest()
    {
        SpoolmanBarcodeMappingRequest request = new() { Barcode = " ", FilamentId = 7 };

        ActionResult<SpoolmanFilamentDto> result = await controller.SaveBarcodeMappingAsync(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SaveBarcodeMappingAsync_ServiceThrows_LogsErrorOutcome()
    {
        SpoolmanBarcodeMappingRequest request = new() { Barcode = "ERR123", FilamentId = 7 };
        spoolmanServiceMock
            .Setup(s => s.SaveBarcodeMappingAsync(7, "ERR123", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Spoolman failed"));

        ActionResult<SpoolmanFilamentDto> result = await controller.SaveBarcodeMappingAsync(request, CancellationToken.None);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, objectResult.StatusCode);
        VerifyLogged(BarcodeScanAction.Mapping, BarcodeScanOutcome.Error, 500, matchedFilamentId: 7);
    }

    [Fact]
    public async Task CreateSpoolByBarcodeAsync_KnownBarcode_ReturnsCreatedSpool()
    {
        SpoolmanImportSpoolByBarcodeRequest request = new()
        {
            Barcode = "ABC123",
            RemainingWeight = 950,
            Location = "Shelf A",
        };
        SpoolmanSpoolDto spool = new(99, "PLA", "PLA", 950, null, false, FilamentId: 7, Location: "Shelf A");
        spoolmanServiceMock
            .Setup(s => s.CreateSpoolByBarcodeAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spool);

        ActionResult<SpoolmanSpoolDto> result = await controller.CreateSpoolByBarcodeAsync(request, CancellationToken.None);

        ObjectResult created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, created.StatusCode);
        SpoolmanSpoolDto value = Assert.IsType<SpoolmanSpoolDto>(created.Value);
        Assert.Equal(7, value.FilamentId);
        Assert.Equal(950, value.RemainingWeightG);
        Assert.Equal("Shelf A", value.Location);
        VerifyLogged(BarcodeScanAction.Import, BarcodeScanOutcome.Imported, 201, matchedFilamentId: 7, createdSpoolId: 99);
    }

    [Fact]
    public async Task CreateSpoolByBarcodeAsync_UnknownBarcode_ReturnsNotFound()
    {
        SpoolmanImportSpoolByBarcodeRequest request = new() { Barcode = "missing" };
        spoolmanServiceMock
            .Setup(s => s.CreateSpoolByBarcodeAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpoolmanSpoolDto?)null);

        ActionResult<SpoolmanSpoolDto> result = await controller.CreateSpoolByBarcodeAsync(request, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        VerifyLogged(BarcodeScanAction.Import, BarcodeScanOutcome.NotFound, 404);
    }

    [Fact]
    public async Task CreateSpoolByBarcodeAsync_ServiceThrows_LogsErrorOutcome()
    {
        SpoolmanImportSpoolByBarcodeRequest request = new() { Barcode = "ERR123" };
        spoolmanServiceMock
            .Setup(s => s.CreateSpoolByBarcodeAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Spoolman failed"));

        ActionResult<SpoolmanSpoolDto> result = await controller.CreateSpoolByBarcodeAsync(request, CancellationToken.None);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, objectResult.StatusCode);
        VerifyLogged(BarcodeScanAction.Import, BarcodeScanOutcome.Error, 500);
    }

    [Fact]
    public async Task BarcodeScanLogService_LogAsync_WhenEnabled_PersistsLog()
    {
        string dbName = Guid.NewGuid().ToString();
        DbContextOptions<AppDbContext> options = CreateDbOptions(dbName);
        BarcodeScanLogService service = CreateLogService(options, enabled: true);

        await service.LogAsync(new BarcodeScanLog
        {
            Barcode = "ABC123",
            Action = BarcodeScanAction.Resolve,
            Outcome = BarcodeScanOutcome.Resolved,
            HttpStatus = 200,
            MatchedFilamentId = 7,
            Message = "resolved",
        });

        await using AppDbContext db = new(options);
        BarcodeScanLog log = Assert.Single(db.BarcodeScanLogs);
        Assert.Equal("ABC123", log.Barcode);
        Assert.Equal(BarcodeScanAction.Resolve, log.Action);
        Assert.Equal(BarcodeScanOutcome.Resolved, log.Outcome);
        Assert.Equal(7, log.MatchedFilamentId);
    }

    [Fact]
    public async Task BarcodeScanLogService_LogAsync_WhenDisabled_DoesNotPersistLog()
    {
        string dbName = Guid.NewGuid().ToString();
        DbContextOptions<AppDbContext> options = CreateDbOptions(dbName);
        BarcodeScanLogService service = CreateLogService(options, enabled: false);

        await service.LogAsync(new BarcodeScanLog
        {
            Barcode = "ABC123",
            Action = BarcodeScanAction.Resolve,
            Outcome = BarcodeScanOutcome.Resolved,
            HttpStatus = 200,
        });

        await using AppDbContext db = new(options);
        Assert.Empty(db.BarcodeScanLogs);
    }

    [Fact]
    public async Task GetBarcodeScanLogsAsync_Admin_ReturnsLogsNewestFirst()
    {
        await using CustomWebApplicationFactory factory = new(new Dictionary<string, string?> { ["Security:DevModeBypassAuth"] = "false" });
        await factory.ResetDatabaseAsync();
        await SeedScanLogsAsync(factory);
        using HttpClient client = await factory.CreateAdminClientAsync();

        HttpResponseMessage response = await client.GetAsync("/api/spoolman/barcodes/scan-logs?limit=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<BarcodeScanLogDto>? logs = await response.Content.ReadFromJsonAsync<List<BarcodeScanLogDto>>(JsonOptions);
        Assert.NotNull(logs);
        Assert.Equal(2, logs.Count);
        Assert.Equal("new", logs[0].Barcode);
        Assert.Equal(BarcodeScanOutcome.Mapped, logs[0].Outcome);
        Assert.Equal("old", logs[1].Barcode);
    }

    [Fact]
    public async Task GetBarcodeScanLogsAsync_Unauthenticated_ReturnsUnauthorized()
    {
        await using CustomWebApplicationFactory factory = new(new Dictionary<string, string?> { ["Security:DevModeBypassAuth"] = "false" });
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/spoolman/barcodes/scan-logs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetBarcodeScanLogsAsync_NonAdmin_ReturnsForbidden()
    {
        await using CustomWebApplicationFactory factory = new(new Dictionary<string, string?> { ["Security:DevModeBypassAuth"] = "false" });
        await factory.ResetDatabaseAsync();
        using HttpClient client = await factory.CreateAuthenticatedClientAsync("barcode-user", "barcode-user@example.com");

        HttpResponseMessage response = await client.GetAsync("/api/spoolman/barcodes/scan-logs");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static SpoolmanFilamentDto CreateFilament(int id, string? articleNumber)
        => new(
            Id: id,
            Name: "PolyTerra PLA",
            Material: "PLA",
            ColorHex: "111111",
            Vendor: "Polymaker",
            Density: 1.24,
            Diameter: 1.75,
            Weight: 1000,
            SpoolWeight: 200,
            Price: 24.99,
            SettingsExtruderTemp: 210,
            SettingsBedTemp: 60,
            ArticleNumber: articleNumber,
            Comment: null,
            MultiColorHexes: null,
            ExternalId: null);

    private void VerifyLogged(
        BarcodeScanAction action,
        BarcodeScanOutcome outcome,
        int httpStatus,
        int? matchedFilamentId = null,
        int? createdSpoolId = null)
    {
        barcodeScanLogServiceMock.Verify(
            s => s.LogAsync(
                It.Is<BarcodeScanLog>(l =>
                    l.Action == action
                    && l.Outcome == outcome
                    && l.HttpStatus == httpStatus
                    && l.MatchedFilamentId == matchedFilamentId
                    && l.CreatedSpoolId == createdSpoolId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static DbContextOptions<AppDbContext> CreateDbOptions(string dbName)
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

    private static BarcodeScanLogService CreateLogService(DbContextOptions<AppDbContext> options, bool enabled)
    {
        Mock<IDbContextFactory<AppDbContext>> dbFactoryMock = new();
        dbFactoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new AppDbContext(options)));

        Mock<ISettingsService> settingsServiceMock = new();
        settingsServiceMock
            .Setup(s => s.Get<SpoolmanSettings>())
            .Returns(new SpoolmanSettings { BarcodeScanDebugLoggingEnabled = enabled });

        Mock<ILogger<BarcodeScanLogService>> loggerMock = new();
        return new BarcodeScanLogService(dbFactoryMock.Object, settingsServiceMock.Object, loggerMock.Object);
    }

    private static async Task SeedScanLogsAsync(CustomWebApplicationFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.BarcodeScanLogs.AddRange(
            new BarcodeScanLog
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-10),
                Barcode = "old",
                Action = BarcodeScanAction.Resolve,
                Outcome = BarcodeScanOutcome.Resolved,
                HttpStatus = 200,
                MatchedFilamentId = 1,
            },
            new BarcodeScanLog
            {
                Timestamp = DateTime.UtcNow,
                Barcode = "new",
                Action = BarcodeScanAction.Mapping,
                Outcome = BarcodeScanOutcome.Mapped,
                HttpStatus = 200,
                MatchedFilamentId = 2,
            });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(
        WebApplicationFactory<Program> factory,
        string username = "test-admin",
        string email = "test@example.com",
        string password = "TestPassword123!")
    {
        using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            IPasswordHashingService passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

            User? existingUser = context.Users.FirstOrDefault(u => u.Username == username);
            if (existingUser is null)
            {
                context.Users.Add(new User
                {
                    Id = Guid.NewGuid(),
                    Username = username,
                    Email = email,
                    PasswordHash = passwordHasher.HashPassword(password),
                    FirstName = "Test",
                    LastName = "Admin",
                    IsActive = true,
                    EmailConfirmed = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                await context.SaveChangesAsync();
            }
        }

        using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
            AuthenticationResult result = await authService.AuthenticateAsync(username, password);
            Assert.True(result.Success);
            Assert.False(string.IsNullOrEmpty(result.Token));

            HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.Token);
            return client;
        }
    }
}
