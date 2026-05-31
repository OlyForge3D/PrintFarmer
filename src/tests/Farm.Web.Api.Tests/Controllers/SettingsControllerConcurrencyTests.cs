using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services;
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
    public async Task UpdateUserSettings_WithoutRowVersion_SucceedsNormally()
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

        // Act: update without row version — should succeed (backward compatible)
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
            Assert.IsType<OkObjectResult>(result);
        }
    }

    [Fact]
    public async Task UpdateUserSettings_ResponseIncludesRowVersion()
    {
        // Arrange
        Guid userId = Guid.NewGuid();
        using (var db = new AppDbContext(_dbOptions))
        {
            var user = new User { Id = userId, Username = "rvtest", Email = "rv@test.com", PasswordHash = "x" };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        // Act: create new settings via PUT (no existing row)
        using (var db = new AppDbContext(_dbOptions))
        {
            var controller = CreateController(db, userId);
            var body = new UpdateUserSettingsBody(
                Theme: "dark",
                Locale: "fr",
                ItemsPerPage: 50,
                DefaultSlicerPreset: null,
                RowVersion: null);

            IActionResult result = await controller.UpdateUserSettingsAsync(body, CancellationToken.None);
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<UserSettingsResponse>(okResult.Value);

            // RowVersion should be present for newly created settings
            Assert.NotNull(response.RowVersion);
            Assert.NotEmpty(response.RowVersion);
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
