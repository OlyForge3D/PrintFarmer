using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services;
using Farm.Modules.Administration.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.Administration.Tests.Controllers;

/// <summary>Account preference contract tests using relational persistence across request contexts.</summary>
public sealed class SettingsControllerPrinterControlModeTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

    public SettingsControllerPrinterControlModeTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task GetUserSettings_NoSettings_ReturnsGuidedWithoutCreatingRow()
    {
        Guid userId = await CreateUserAsync();
        UserSettingsResponse response = await GetAsync(userId);

        Assert.Equal(userId, response.UserId);
        Assert.Equal("Guided", response.PrinterControlMode);
        Assert.Null(response.RowVersion);
        using var db = new AppDbContext(_options);
        Assert.Empty(await db.UserSettings.ToListAsync());
        Assert.Equal("Guided", new UserSettings().PrinterControlMode);
    }

    [Theory]
    [InlineData("{}", "Guided")]
    [InlineData("{\"printerControlMode\":\"Guided\"}", "Guided")]
    [InlineData("{\"printerControlMode\":\"Expert\"}", "Expert")]
    public async Task UpdateUserSettings_FirstWrite_PersistsModeAcrossContexts(string json, string expected)
    {
        Guid userId = await CreateUserAsync();
        UserSettingsResponse saved = Response(await PutAsync(userId, Deserialize(json)));
        Assert.Equal(expected, saved.PrinterControlMode);
        Assert.NotNull(saved.RowVersion);

        UserSettingsResponse reloaded = await GetAsync(userId);
        Assert.Equal(expected, reloaded.PrinterControlMode);
        Assert.Equal(saved.RowVersion, reloaded.RowVersion);
        using var db = new AppDbContext(_options);
        Assert.Equal(expected, (await db.UserSettings.SingleAsync()).PrinterControlMode);
    }

    [Theory]
    [InlineData("{\"theme\":\"dark\"}")]
    [InlineData("{\"theme\":\"dark\",\"printerControlMode\":null}")]
    public async Task UpdateUserSettings_LegacyOrNullMode_PreservesExpert(string json)
    {
        Guid userId = await CreateUserAsync();
        UserSettingsResponse initial = Response(await PutAsync(userId, Body("Expert")));
        UpdateUserSettingsBody legacyBody = Deserialize(json) with { RowVersion = initial.RowVersion };

        UserSettingsResponse saved = Response(await PutAsync(userId, legacyBody));

        Assert.Equal("Expert", saved.PrinterControlMode);
        Assert.NotEqual(initial.RowVersion, saved.RowVersion);
        UserSettingsResponse reloaded = await GetAsync(userId);
        Assert.Equal("Expert", reloaded.PrinterControlMode);
        Assert.Equal("dark", reloaded.Theme);
    }

    [Fact]
    public async Task UpdateUserSettings_ModeOnlyJson_PreservesAllOtherAccountSettings()
    {
        Guid userId = await CreateUserAsync();
        var settings = new UserSettings
        {
            UserId = userId,
            Theme = "dark",
            Locale = "fr",
            ItemsPerPage = 75,
            DefaultSlicerPreset = "custom-quality-preset",
            PrintablesUsername = "existing-printables-user",
            PrintablesOAuthAccessToken = "test-access-token",
            PrintablesOAuthRefreshToken = "test-refresh-token",
            PrintablesOAuthTokenType = "Bearer",
            PrintablesOAuthScope = "read",
            PrintablesOAuthTokenExpiresAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            PrintablesOAuthLinkedAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        using (var db = new AppDbContext(_options))
        {
            db.UserSettings.Add(settings);
            await db.SaveChangesAsync();
        }

        UserSettingsResponse initial = await GetAsync(userId);
        // Match the frontend payload exactly: no unrelated settings keys, not even null values.
        UpdateUserSettingsBody body = Deserialize(
            $"{{\"printerControlMode\":\"Expert\",\"rowVersion\":\"{initial.RowVersion}\"}}");
        UserSettingsResponse saved = Response(await PutAsync(userId, body));

        Assert.NotEqual(initial.RowVersion, saved.RowVersion);
        Assert.Equal(initial with { PrinterControlMode = "Expert", RowVersion = saved.RowVersion }, saved);
        Assert.Equal(saved, await GetAsync(userId));
        using var reloadedDb = new AppDbContext(_options);
        UserSettings reloaded = await reloadedDb.UserSettings.SingleAsync(u => u.UserId == userId);
        Assert.Equal(settings.Id, reloaded.Id);
        Assert.Equal(settings.PrintablesOAuthAccessToken, reloaded.PrintablesOAuthAccessToken);
        Assert.Equal(settings.PrintablesOAuthRefreshToken, reloaded.PrintablesOAuthRefreshToken);
        Assert.Equal(settings.PrintablesOAuthTokenType, reloaded.PrintablesOAuthTokenType);
        Assert.Equal(settings.PrintablesOAuthScope, reloaded.PrintablesOAuthScope);
        Assert.Equal(settings.PrintablesOAuthTokenExpiresAtUtc, reloaded.PrintablesOAuthTokenExpiresAtUtc);
        Assert.Equal(settings.PrintablesOAuthLinkedAtUtc, reloaded.PrintablesOAuthLinkedAtUtc);
    }

    [Fact]
    public async Task UpdateUserSettings_ExpertToGuided_PersistsExplicitChoice()
    {
        Guid userId = await CreateUserAsync();
        UserSettingsResponse initial = Response(await PutAsync(userId, Body("Expert")));
        UserSettingsResponse saved = Response(await PutAsync(userId, Body("Guided", initial.RowVersion)));

        Assert.Equal("Guided", saved.PrinterControlMode);
        Assert.NotEqual(initial.RowVersion, saved.RowVersion);
        Assert.Equal("Guided", (await GetAsync(userId)).PrinterControlMode);
    }

    [Fact]
    public async Task UpdateUserSettings_DifferentAccounts_OnlyChangesAuthenticatedAccount()
    {
        Guid firstUser = await CreateUserAsync();
        Guid secondUser = await CreateUserAsync();
        UserSettingsResponse second = Response(await PutAsync(secondUser, Body("Guided")));
        // A body-supplied account ID cannot redirect the authenticated user's write.
        UpdateUserSettingsBody body = Deserialize($"{{\"printerControlMode\":\"Expert\",\"userId\":\"{secondUser}\"}}");
        UserSettingsResponse first = Response(await PutAsync(firstUser, body));

        Assert.Equal(firstUser, first.UserId);
        Assert.Equal("Expert", (await GetAsync(firstUser)).PrinterControlMode);
        Assert.Equal(second, await GetAsync(secondUser));
        using var db = new AppDbContext(_options);
        Assert.Equal(2, await db.UserSettings.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("guided")]
    [InlineData("EXPERT")]
    [InlineData(" Expert ")]
    [InlineData("Advanced")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("999")]
    public async Task UpdateUserSettings_InvalidMode_Returns400WithoutPersisting(string mode)
    {
        Guid userId = await CreateUserAsync();
        Assert.IsType<BadRequestObjectResult>(await PutAsync(userId, Body(mode)));
        using (var db = new AppDbContext(_options))
        {
            Assert.Empty(await db.UserSettings.ToListAsync());
        }

        UserSettingsResponse initial = Response(await PutAsync(userId, Body("Expert")));
        Assert.IsType<BadRequestObjectResult>(await PutAsync(userId, Body(mode, initial.RowVersion)));
        Assert.Equal(initial, await GetAsync(userId));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("999")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Deserialize_NonStringMode_RejectsInvalidJsonType(string value)
    {
        Assert.Throws<JsonException>(() => Deserialize($"{{\"printerControlMode\":{value}}}"));
    }

    [Fact]
    public async Task UpdateUserSettings_StaleRowVersion_Returns409AndPreservesLatestMode()
    {
        Guid userId = await CreateUserAsync();
        UserSettingsResponse initial = Response(await PutAsync(userId, Body("Guided")));
        UserSettingsResponse latest = Response(await PutAsync(userId, Body("Expert", initial.RowVersion)));

        Assert.IsType<ConflictObjectResult>(await PutAsync(userId, Body("Guided", initial.RowVersion)));
        // An older client with stale settings must not overwrite the newer account preference either.
        var legacyBody = Deserialize("{\"theme\":\"dark\"}") with { RowVersion = initial.RowVersion };
        Assert.IsType<ConflictObjectResult>(await PutAsync(userId, legacyBody));
        Assert.Equal(latest, await GetAsync(userId));
    }

    [Fact]
    public async Task UpdateUserSettings_MissingRowVersion_Returns428WithoutChangingMode()
    {
        Guid userId = await CreateUserAsync();
        UserSettingsResponse initial = Response(await PutAsync(userId, Body("Expert")));
        var result = Assert.IsType<ObjectResult>(await PutAsync(userId, Body("Guided")));

        Assert.Equal(StatusCodes.Status428PreconditionRequired, result.StatusCode);
        Assert.Equal(initial, await GetAsync(userId));
    }

    [Fact]
    public async Task UpdateUserSettings_IfMatchHeader_UsesExistingConcurrencyContract()
    {
        Guid userId = await CreateUserAsync();
        UserSettingsResponse initial = Response(await PutAsync(userId, Body("Guided")));
        using var db = new AppDbContext(_options);
        SettingsController controller = CreateController(db, userId);
        controller.Request.Headers.IfMatch = $"\"{initial.RowVersion}\"";

        UserSettingsResponse saved = Response(await controller.UpdateUserSettingsAsync(Body("Expert"), CancellationToken.None));

        Assert.Equal("Expert", saved.PrinterControlMode);
        Assert.NotEqual(initial.RowVersion, saved.RowVersion);
        Assert.Equal(saved, await GetAsync(userId));
    }

    [Theory]
    [InlineData("Guided")]
    [InlineData("Expert")]
    public async Task Serialize_Response_UsesCamelCaseStringMode(string mode)
    {
        Guid userId = await CreateUserAsync();
        UserSettingsResponse saved = Response(await PutAsync(userId, Body(mode)));
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(saved, JsonOptions));

        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("printerControlMode").ValueKind);
        Assert.Equal(mode, json.RootElement.GetProperty("printerControlMode").GetString());
        Assert.Equal(saved.RowVersion, json.RootElement.GetProperty("rowVersion").GetString());
        Assert.False(json.RootElement.TryGetProperty("PrinterControlMode", out _));
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private async Task<Guid> CreateUserAsync()
    {
        Guid userId = Guid.NewGuid();
        using var db = new AppDbContext(_options);
        db.Users.Add(new User { Id = userId, Username = userId.ToString(), Email = $"{userId}@test.com", PasswordHash = "x" });
        await db.SaveChangesAsync();
        return userId;
    }

    private async Task<UserSettingsResponse> GetAsync(Guid userId)
    {
        using var db = new AppDbContext(_options);
        return Response(await CreateController(db, userId).GetUserSettingsAsync(CancellationToken.None));
    }

    private async Task<IActionResult> PutAsync(Guid userId, UpdateUserSettingsBody body)
    {
        using var db = new AppDbContext(_options);
        return await CreateController(db, userId).UpdateUserSettingsAsync(body, CancellationToken.None);
    }

    private static SettingsController CreateController(AppDbContext db, Guid userId) =>
        new(Mock.Of<IFarmSettingsService>(), db, NullLogger<SettingsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId.ToString())], "test"))
                }
            }
        };

    private static UpdateUserSettingsBody Body(string mode, string? rowVersion = null) =>
        new(null, null, null, null, RowVersion: rowVersion, PrinterControlMode: mode);

    private static UpdateUserSettingsBody Deserialize(string json) =>
        JsonSerializer.Deserialize<UpdateUserSettingsBody>(json, JsonOptions)!;

    private static UserSettingsResponse Response(IActionResult result) =>
        Assert.IsType<UserSettingsResponse>(Assert.IsType<OkObjectResult>(result).Value);
}
