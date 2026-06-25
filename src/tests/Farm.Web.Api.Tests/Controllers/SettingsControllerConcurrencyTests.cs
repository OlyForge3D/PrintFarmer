using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Tests optimistic concurrency enforcement on settings PUT endpoints.
/// Uses SQLite with a kept-alive connection for realistic EF behavior.
/// </summary>
public class SettingsControllerConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly Mock<IFarmSettingsService> _farmSettingsMock;
    private readonly Mock<ILogger<SettingsController>> _loggerMock;

    public SettingsControllerConcurrencyTests()
    {
        // Keep connection open so in-memory SQLite DB persists across contexts
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new AppDbContext(_dbOptions);
        db.Database.EnsureCreated();

        _farmSettingsMock = new Mock<IFarmSettingsService>();
        _loggerMock = new Mock<ILogger<SettingsController>>();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task UpdateUserSettings_FirstTimeCreateWithoutConcurrencyToken_ReturnsOk()
    {
        Guid userId = Guid.NewGuid();

        using (var db = new AppDbContext(_dbOptions))
        {
            db.Users.Add(new User { Id = userId, Username = "newuser", Email = "new@test.com", PasswordHash = "x" });
            await db.SaveChangesAsync();
        }

        using (var db = new AppDbContext(_dbOptions))
        {
            var controller = CreateController(db, userId);
            var body = new UpdateUserSettingsBody(
                Theme: "light",
                Locale: "en",
                ItemsPerPage: 30,
                DefaultSlicerPreset: null,
                RowVersion: null);

            IActionResult result = await controller.UpdateUserSettingsAsync(body, CancellationToken.None);
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<UserSettingsResponse>(okResult.Value);
            Assert.Equal("light", response.Theme);
            Assert.NotNull(response.RowVersion);
        }

        using (var db = new AppDbContext(_dbOptions))
        {
            UserSettings? created = await db.UserSettings.FirstOrDefaultAsync(u => u.UserId == userId);
            Assert.NotNull(created);
            Assert.Equal("light", created!.Theme);
        }
    }

    [Fact]
    public async Task UpdateUserSettings_ConcurrentWrites_SecondReturns409()
    {
        // Arrange: seed a user settings row
        Guid userId = Guid.NewGuid();
        byte[] originalRowVersion;

        using (var db = new AppDbContext(_dbOptions))
        {
            var user = new User { Id = userId, Username = "test", Email = "test@test.com", PasswordHash = "x" };
            db.Users.Add(user);
            var settings = new UserSettings
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Theme = "dark",
                Locale = "en",
                ItemsPerPage = 25
            };
            db.UserSettings.Add(settings);
            await db.SaveChangesAsync();
            originalRowVersion = settings.RowVersion;
        }

        Assert.NotNull(originalRowVersion);
        Assert.NotEmpty(originalRowVersion);
        string rowVersionBase64 = Convert.ToBase64String(originalRowVersion);

        // Act: First writer updates successfully
        using (var db1 = new AppDbContext(_dbOptions))
        {
            var controller1 = CreateController(db1, userId);
            var body1 = new UpdateUserSettingsBody(
                Theme: "light",
                Locale: null,
                ItemsPerPage: null,
                DefaultSlicerPreset: null,
                RowVersion: rowVersionBase64);

            IActionResult result1 = await controller1.UpdateUserSettingsAsync(body1, CancellationToken.None);
            Assert.IsType<OkObjectResult>(result1);
        }

        // Act: Second writer uses the SAME original row version (now stale)
        using (var db2 = new AppDbContext(_dbOptions))
        {
            var controller2 = CreateController(db2, userId);
            var body2 = new UpdateUserSettingsBody(
                Theme: "blue",
                Locale: null,
                ItemsPerPage: null,
                DefaultSlicerPreset: null,
                RowVersion: rowVersionBase64);

            IActionResult result2 = await controller2.UpdateUserSettingsAsync(body2, CancellationToken.None);

            // Assert: second write gets 409 Conflict due to stale concurrency token
            Assert.IsType<ConflictObjectResult>(result2);
        }
    }

    [Fact]
    public async Task UpdateUserSettings_MalformedIfMatchHeader_Returns400()
    {
        Guid userId = Guid.NewGuid();

        using (var db = new AppDbContext(_dbOptions))
        {
            var user = new User { Id = userId, Username = "badheader", Email = "header@test.com", PasswordHash = "x" };
            db.Users.Add(user);
            db.UserSettings.Add(new UserSettings
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Theme = "dark",
                Locale = "en",
                ItemsPerPage = 25
            });
            await db.SaveChangesAsync();
        }

        using (var db = new AppDbContext(_dbOptions))
        {
            var controller = CreateController(db, userId);
            controller.ControllerContext.HttpContext.Request.Headers.IfMatch = "\"not-base64@@\"";
            var body = new UpdateUserSettingsBody(
                Theme: "light",
                Locale: null,
                ItemsPerPage: null,
                DefaultSlicerPreset: null,
                RowVersion: null);

            IActionResult result = await controller.UpdateUserSettingsAsync(body, CancellationToken.None);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
        }
    }

    [Fact]
    public async Task UpdateUserSettings_InvalidBodyRowVersion_Returns400()
    {
        Guid userId = Guid.NewGuid();

        using (var db = new AppDbContext(_dbOptions))
        {
            var user = new User { Id = userId, Username = "badbody", Email = "body@test.com", PasswordHash = "x" };
            db.Users.Add(user);
            db.UserSettings.Add(new UserSettings
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Theme = "dark",
                Locale = "en",
                ItemsPerPage = 25
            });
            await db.SaveChangesAsync();
        }

        using (var db = new AppDbContext(_dbOptions))
        {
            var controller = CreateController(db, userId);
            var body = new UpdateUserSettingsBody(
                Theme: "light",
                Locale: null,
                ItemsPerPage: null,
                DefaultSlicerPreset: null,
                RowVersion: "not-base64");

            IActionResult result = await controller.UpdateUserSettingsAsync(body, CancellationToken.None);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
        }
    }

    [Fact]
    public async Task UpdateUserSettings_WithoutConcurrencyToken_Returns428()
    {
        // Arrange
        Guid userId = Guid.NewGuid();

        using (var db = new AppDbContext(_dbOptions))
        {
            var user = new User { Id = userId, Username = "noversion", Email = "no@test.com", PasswordHash = "x" };
            db.Users.Add(user);
            var settings = new UserSettings
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Theme = "dark",
                Locale = "en",
                ItemsPerPage = 25
            };
            db.UserSettings.Add(settings);
            await db.SaveChangesAsync();
        }

        // Act: update without If-Match and without rowVersion
        using (var db = new AppDbContext(_dbOptions))
        {
            var controller = CreateController(db, userId);
            var body = new UpdateUserSettingsBody(
                Theme: "light",
                Locale: null,
                ItemsPerPage: null,
                DefaultSlicerPreset: null,
                RowVersion: null);

            IActionResult result = await controller.UpdateUserSettingsAsync(body, CancellationToken.None);
            var precondition = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status428PreconditionRequired, precondition.StatusCode);
        }
    }

    [Fact]
    public void UpdateFarmSettings_FirstTimeCreateWithoutConcurrencyToken_ReturnsOk()
    {
        Guid userId = Guid.NewGuid();
        _farmSettingsMock.Setup(x => x.GetFarmSettingsRowVersion()).Returns((string?)null);
        _farmSettingsMock.Setup(x => x.GetFarmSettings()).Returns(new FarmSettingsDto(0.15m, 1.25m, 120m, false, SlicerMode.Simple, new[] { SlicerMode.Simple }));

        using var db = new AppDbContext(_dbOptions);
        var controller = CreateController(db, userId);
        var body = new UpdateFarmSettingsBody(0.2m, 2.0m, 140m);

        IActionResult result = controller.UpdateFarmSettings(body);

        Assert.IsType<OkObjectResult>(result);
        _farmSettingsMock.Verify(
            x => x.UpdateFarmSettings(
                It.Is<UpdateFarmSettingsRequest>(r =>
                    r.ElectricityRatePerKwh == 0.2m &&
                    r.DefaultMachineHourlyRate == 2.0m &&
                    r.AveragePrinterWattage == 140m),
                null),
            Times.Once);
    }

    [Fact]
    public void UpdateFarmSettings_WhenRowExists_WithoutConcurrencyToken_Returns428()
    {
        Guid userId = Guid.NewGuid();
        _farmSettingsMock.Setup(x => x.GetFarmSettingsRowVersion()).Returns(Convert.ToBase64String([1, 2, 3, 4]));

        using var db = new AppDbContext(_dbOptions);
        var controller = CreateController(db, userId);
        var body = new UpdateFarmSettingsBody(0.2m, 2.0m, 140m);

        IActionResult result = controller.UpdateFarmSettings(body);

        var precondition = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status428PreconditionRequired, precondition.StatusCode);
        _farmSettingsMock.Verify(
            x => x.UpdateFarmSettings(It.IsAny<UpdateFarmSettingsRequest>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateUserSettings_ResponseIncludesRowVersion()
    {
        // Arrange
        Guid userId = Guid.NewGuid();
        string existingRowVersion;
        using (var db = new AppDbContext(_dbOptions))
        {
            var user = new User { Id = userId, Username = "rvtest", Email = "rv@test.com", PasswordHash = "x" };
            db.Users.Add(user);
            var settings = new UserSettings
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Theme = "dark",
                Locale = "en",
                ItemsPerPage = 25
            };
            db.UserSettings.Add(settings);
            await db.SaveChangesAsync();
            existingRowVersion = Convert.ToBase64String(settings.RowVersion);
        }

        // Act
        using (var db = new AppDbContext(_dbOptions))
        {
            var controller = CreateController(db, userId);
            var body = new UpdateUserSettingsBody(
                Theme: "dark",
                Locale: "fr",
                ItemsPerPage: 50,
                DefaultSlicerPreset: null,
                RowVersion: existingRowVersion);

            IActionResult result = await controller.UpdateUserSettingsAsync(body, CancellationToken.None);
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<UserSettingsResponse>(okResult.Value);

            // RowVersion should be present after a successful update
            Assert.NotNull(response.RowVersion);
            Assert.NotEmpty(response.RowVersion);
        }
    }

    [Fact]
    public async Task UpdateUserSettings_WithPrintablesUsername_PersistsTrimmedValue()
    {
        Guid userId = Guid.NewGuid();
        using (var db = new AppDbContext(_dbOptions))
        {
            var user = new User { Id = userId, Username = "printables", Email = "printables@test.com", PasswordHash = "x" };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        string createdRowVersion;
        using (var db = new AppDbContext(_dbOptions))
        {
            var controller = CreateController(db, userId);
            var createBody = new UpdateUserSettingsBody(
                Theme: "dark",
                Locale: "en",
                ItemsPerPage: 25,
                DefaultSlicerPreset: null,
                RowVersion: null,
                PrintablesUsername: "  ripley-user  ");

            IActionResult createResult = await controller.UpdateUserSettingsAsync(createBody, CancellationToken.None);
            var okCreate = Assert.IsType<OkObjectResult>(createResult);
            var createResponse = Assert.IsType<UserSettingsResponse>(okCreate.Value);
            Assert.Equal("ripley-user", createResponse.PrintablesUsername);
            createdRowVersion = createResponse.RowVersion!;
        }

        using (var db = new AppDbContext(_dbOptions))
        {
            var controller = CreateController(db, userId);
            var clearBody = new UpdateUserSettingsBody(
                Theme: null,
                Locale: null,
                ItemsPerPage: null,
                DefaultSlicerPreset: null,
                RowVersion: createdRowVersion,
                PrintablesUsername: string.Empty);

            IActionResult clearResult = await controller.UpdateUserSettingsAsync(clearBody, CancellationToken.None);
            var okClear = Assert.IsType<OkObjectResult>(clearResult);
            var clearResponse = Assert.IsType<UserSettingsResponse>(okClear.Value);
            Assert.Null(clearResponse.PrintablesUsername);
        }
    }

    [Fact]
    public async Task UpdateUserSettings_WithPrintablesUsernameStartingWithAt_Returns400()
    {
        Guid userId = Guid.NewGuid();
        using (var db = new AppDbContext(_dbOptions))
        {
            var user = new User { Id = userId, Username = "printablesat", Email = "printablesat@test.com", PasswordHash = "x" };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        using (var db = new AppDbContext(_dbOptions))
        {
            var controller = CreateController(db, userId);
            var body = new UpdateUserSettingsBody(
                Theme: "dark",
                Locale: "en",
                ItemsPerPage: 25,
                DefaultSlicerPreset: null,
                RowVersion: null,
                PrintablesUsername: "@ripley-user");

            IActionResult result = await controller.UpdateUserSettingsAsync(body, CancellationToken.None);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
            Assert.Equal("printablesUsername must not start with '@'.", badRequest.Value);
        }

        using (var db = new AppDbContext(_dbOptions))
        {
            UserSettings? created = await db.UserSettings.FirstOrDefaultAsync(u => u.UserId == userId);
            Assert.Null(created);
        }
    }

    private SettingsController CreateController(AppDbContext db, Guid userId)
    {
        var controller = new SettingsController(
            _farmSettingsMock.Object,
            db,
            _loggerMock.Object);

        var claims = new[] { new Claim("sub", userId.ToString()) };
        var identity = new ClaimsIdentity(claims, "test");
        var principal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return controller;
    }
}
