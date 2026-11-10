using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Modules.Inventory.Controllers;
using Farm.Web.Api.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.Inventory.Tests.Controllers;

/// <summary>
/// Remembered non-GTIN scan codes (issue: FNSKU aliases). The user's real Amazon FNSKU
/// <c>X0022Z40I1</c> is the primary fixture.
/// </summary>
public class ScanCodeAliasTests
{
    private const string Fnsku = "X0022Z40I1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class AliasFactory(Mock<ISpoolmanService> spoolman)
        : CustomWebApplicationFactory(new Dictionary<string, string?> { ["Security:DevModeBypassAuth"] = "false" })
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISpoolmanService>();
                services.AddSingleton(spoolman.Object);
            });
        }
    }

    // ---- Validator -------------------------------------------------------------------------

    [Theory]
    [InlineData(Fnsku)]
    [InlineData("  X0022Z40I1  ")]
    [InlineData("B08XYZ1234")]
    [InlineData("CODE39-ABC")]
    [InlineData("https://example.com/spool")]
    [InlineData("LOT/850078714923")]
    [InlineData("00224X")]
    public void Validate_NonGtinCodes_AreKeptVerbatim(string code)
    {
        string? result = ScanCodeAliasValidator.Validate(code, out string? error);

        Assert.Null(error);
        Assert.Equal(code.Trim(), result);
    }

    [Fact]
    public void Validate_Fnsku_IsNeverReducedToItsDigits()
    {
        Assert.False(ScanCodeAliasValidator.IsGtinShaped(Fnsku));
        Assert.Equal(Fnsku, ScanCodeAliasValidator.Validate(Fnsku, out _));
        Assert.NotEqual("00224", ScanCodeAliasValidator.Validate(Fnsku, out _));
    }

    [Theory]
    [InlineData("012345678905")]
    [InlineData("4006381333931")]
    [InlineData("96385074")]
    [InlineData("0-12345-67890-5")]
    [InlineData("00012345678905")]
    public void Validate_GtinShapedCodes_AreRejected(string code)
    {
        Assert.Null(ScanCodeAliasValidator.Validate(code, out string? error));
        Assert.Contains("GTIN", error);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("00224")]
    [InlineData("012345678906")]
    [InlineData("1-2 3")]
    public void Validate_DigitOnlyNonGtinCodes_AreRejected(string code)
    {
        Assert.Null(ScanCodeAliasValidator.Validate(code, out string? error));
        Assert.Contains("digits", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("X0022\u0000Z40I1")]
    [InlineData("X0022\u200BZ40I1")]
    public void Validate_EmptyOrControlCharacters_AreRejected(string? code)
    {
        Assert.Null(ScanCodeAliasValidator.Validate(code, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_TooLong_IsRejected()
    {
        Assert.Null(ScanCodeAliasValidator.Validate(new string('A', ScanCodeAliasValidator.MaxLength + 1), out _));
        Assert.NotNull(ScanCodeAliasValidator.Validate(new string('A', ScanCodeAliasValidator.MaxLength), out _));
    }

    // ---- Service over real SQLite storage with the real migrations ----------------------------

    [Fact]
    public async Task Service_SavedAlias_SurvivesRestartAndResolvesExactly()
    {
        using TempSqliteDatabase database = await TempSqliteDatabase.CreateAsync();
        Mock<ISpoolmanService> spoolman = SpoolmanWith(CreateFilament(42));

        ScanCodeAliasSaveResult saved = await database.CreateService(spoolman).SaveAsync(Fnsku, 42, "user-1", CancellationToken.None);
        Assert.Equal(ScanCodeAliasSaveStatus.Created, saved.Status);

        // A brand-new service and connection simulates an API restart.
        ScanCodeAliasService afterRestart = database.CreateService(spoolman);
        ScanCodeAliasLookupResult found = await afterRestart.LookupAsync(Fnsku, CancellationToken.None);
        Assert.Equal(ScanCodeAliasLookupStatus.Found, found.Status);
        Assert.Equal(42, found.Filament!.Id);
        Assert.Equal(Fnsku, found.Alias!.Code);
        Assert.Equal("user-1", found.Alias.CreatedByUserId);

        Assert.Equal(ScanCodeAliasLookupStatus.NotFound, (await afterRestart.LookupAsync("x0022z40i1", CancellationToken.None)).Status);
        Assert.Equal(ScanCodeAliasLookupStatus.NotFound, (await afterRestart.LookupAsync("X0022Z40I", CancellationToken.None)).Status);
        Assert.Equal(ScanCodeAliasLookupStatus.NotFound, (await afterRestart.LookupAsync("00224", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Service_SaveDoesNotTouchSpoolmanGtinMapping()
    {
        using TempSqliteDatabase database = await TempSqliteDatabase.CreateAsync();
        Mock<ISpoolmanService> spoolman = SpoolmanWith(CreateFilament(42));

        _ = await database.CreateService(spoolman).SaveAsync(Fnsku, 42, null, CancellationToken.None);

        spoolman.Verify(s => s.SaveBarcodeMappingAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        spoolman.Verify(s => s.UpdateFilamentAsync(It.IsAny<int>(), It.IsAny<SpoolmanUpdateFilamentRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Service_SameFilamentIsIdempotent_DifferentFilamentConflicts()
    {
        using TempSqliteDatabase database = await TempSqliteDatabase.CreateAsync();
        Mock<ISpoolmanService> spoolman = SpoolmanWith(CreateFilament(42), CreateFilament(43));
        ScanCodeAliasService service = database.CreateService(spoolman);

        Assert.Equal(ScanCodeAliasSaveStatus.Created, (await service.SaveAsync(Fnsku, 42, null, CancellationToken.None)).Status);
        Assert.Equal(ScanCodeAliasSaveStatus.AlreadyMapped, (await service.SaveAsync(Fnsku, 42, null, CancellationToken.None)).Status);

        ScanCodeAliasSaveResult conflict = await service.SaveAsync(Fnsku, 43, null, CancellationToken.None);
        Assert.Equal(ScanCodeAliasSaveStatus.Conflict, conflict.Status);
        Assert.Equal(42, conflict.Alias!.FilamentId);

        Assert.Equal(42, (await service.LookupAsync(Fnsku, CancellationToken.None)).Filament!.Id);
        Assert.Equal(1, await database.CountAsync());
    }

    [Fact]
    public async Task Service_CaseVariantsAreDistinctAliases()
    {
        using TempSqliteDatabase database = await TempSqliteDatabase.CreateAsync();
        Mock<ISpoolmanService> spoolman = SpoolmanWith(CreateFilament(42), CreateFilament(43));
        ScanCodeAliasService service = database.CreateService(spoolman);

        Assert.Equal(ScanCodeAliasSaveStatus.Created, (await service.SaveAsync(Fnsku, 42, null, CancellationToken.None)).Status);
        Assert.Equal(ScanCodeAliasSaveStatus.Created, (await service.SaveAsync("x0022z40i1", 43, null, CancellationToken.None)).Status);

        Assert.Equal(42, (await service.LookupAsync(Fnsku, CancellationToken.None)).Filament!.Id);
        Assert.Equal(43, (await service.LookupAsync("x0022z40i1", CancellationToken.None)).Filament!.Id);
    }

    [Fact]
    public async Task Service_StaleAliasForDeletedFilamentIsReplaced()
    {
        using TempSqliteDatabase database = await TempSqliteDatabase.CreateAsync();
        Mock<ISpoolmanService> spoolman = SpoolmanWith(CreateFilament(42), CreateFilament(43));
        ScanCodeAliasService service = database.CreateService(spoolman);
        _ = await service.SaveAsync(Fnsku, 42, null, CancellationToken.None);

        spoolman.Setup(s => s.FindFilamentByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync((SpoolmanFilamentDto?)null);
        Assert.Equal(ScanCodeAliasLookupStatus.FilamentMissing, (await service.LookupAsync(Fnsku, CancellationToken.None)).Status);

        Assert.Equal(ScanCodeAliasSaveStatus.Replaced, (await service.SaveAsync(Fnsku, 43, null, CancellationToken.None)).Status);
        Assert.Equal(43, (await service.LookupAsync(Fnsku, CancellationToken.None)).Filament!.Id);
        Assert.Equal(1, await database.CountAsync());
    }

    [Fact]
    public async Task Service_SpoolmanFailures_PropagateAndWriteNothing()
    {
        using TempSqliteDatabase database = await TempSqliteDatabase.CreateAsync();
        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(s => s.FindFilamentByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SpoolmanApiException("down"));
        ScanCodeAliasService service = database.CreateService(spoolman);

        _ = await Assert.ThrowsAsync<SpoolmanApiException>(() => service.SaveAsync(Fnsku, 42, null, CancellationToken.None));
        Assert.Equal(0, await database.CountAsync());
    }

    [Fact]
    public async Task Service_MissingFilamentOrGtin_WritesNothing()
    {
        using TempSqliteDatabase database = await TempSqliteDatabase.CreateAsync();
        Mock<ISpoolmanService> spoolman = SpoolmanWith(CreateFilament(42));
        ScanCodeAliasService service = database.CreateService(spoolman);

        Assert.Equal(ScanCodeAliasSaveStatus.FilamentNotFound, (await service.SaveAsync(Fnsku, 99, null, CancellationToken.None)).Status);
        Assert.Equal(ScanCodeAliasSaveStatus.Invalid, (await service.SaveAsync("012345678905", 42, null, CancellationToken.None)).Status);
        Assert.Equal(ScanCodeAliasSaveStatus.Invalid, (await service.SaveAsync(Fnsku, 0, null, CancellationToken.None)).Status);
        Assert.Equal(0, await database.CountAsync());
    }

    [Fact]
    public async Task Service_UniqueIndexRejectsDuplicateCodes()
    {
        using TempSqliteDatabase database = await TempSqliteDatabase.CreateAsync();
        await using AppDbContext db = database.CreateContext();
        db.ScanCodeAliases.Add(new ScanCodeAlias { Code = Fnsku, FilamentId = 1 });
        db.ScanCodeAliases.Add(new ScanCodeAlias { Code = Fnsku, FilamentId = 2 });

        _ = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ---- HTTP contract -------------------------------------------------------------------

    [Fact]
    public async Task Http_FirstScanMisses_SaveThenFreshClientResolves()
    {
        Mock<ISpoolmanService> spoolman = SpoolmanWith(CreateFilament(42));
        await using AliasFactory factory = new(spoolman);
        await factory.ResetDataAsync();
        using HttpClient admin = await factory.CreateAdminClientAsync();

        Assert.Equal(ScanCodeAliasLookupState.NotRemembered, (await LookupAsync(admin, Fnsku)).State);

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/spoolman/code-aliases", new { code = Fnsku, filamentId = 42 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        ScanCodeAliasDto? body = await created.Content.ReadFromJsonAsync<ScanCodeAliasDto>(JsonOptions);
        Assert.Equal(Fnsku, body!.Code);
        Assert.Equal(42, body.FilamentId);
        Assert.Equal(42, body.Filament!.Id);

        using HttpClient fresh = await factory.CreateAuthenticatedClientAsync("scan-user", "scan-user@example.com");
        ScanCodeAliasLookupResponse hit = await LookupAsync(fresh, Fnsku);
        Assert.Equal(ScanCodeAliasLookupState.Found, hit.State);
        Assert.Equal(42, hit.Alias!.Filament!.Id);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        ScanCodeAlias stored = await db.ScanCodeAliases.AsNoTracking().SingleAsync();
        Assert.Equal(Fnsku, stored.Code);
        Assert.Equal(42, stored.FilamentId);
        Assert.False(string.IsNullOrEmpty(stored.CreatedByUserId));
        spoolman.Verify(s => s.SaveBarcodeMappingAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Http_RepeatSaveIsOk_ConflictIs409AndKeepsOriginal()
    {
        Mock<ISpoolmanService> spoolman = SpoolmanWith(CreateFilament(42), CreateFilament(43));
        await using AliasFactory factory = new(spoolman);
        await factory.ResetDataAsync();
        using HttpClient admin = await factory.CreateAdminClientAsync();

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/spoolman/code-aliases", new { code = Fnsku, filamentId = 42 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/spoolman/code-aliases", new { code = Fnsku, filamentId = 42 })).StatusCode);

        HttpResponseMessage conflict = await admin.PostAsJsonAsync("/api/spoolman/code-aliases", new { code = Fnsku, filamentId = 43 });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        ScanCodeAliasDto? existing = await conflict.Content.ReadFromJsonAsync<ScanCodeAliasDto>(JsonOptions);
        Assert.Equal(42, existing!.FilamentId);

        Assert.Equal(42, (await LookupAsync(admin, Fnsku)).Alias!.FilamentId);
    }

    [Theory]
    [InlineData("012345678905", 42)]
    [InlineData("", 42)]
    [InlineData(Fnsku, 0)]
    public async Task Http_InvalidSave_Returns400(string code, int filamentId)
    {
        Mock<ISpoolmanService> spoolman = SpoolmanWith(CreateFilament(42));
        await using AliasFactory factory = new(spoolman);
        await factory.ResetDataAsync();
        using HttpClient admin = await factory.CreateAdminClientAsync();

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/spoolman/code-aliases", new { code, filamentId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Http_GtinLookup_Returns400AndNeverMatchesFnskuDigits()
    {
        Mock<ISpoolmanService> spoolman = SpoolmanWith(CreateFilament(42));
        await using AliasFactory factory = new(spoolman);
        await factory.ResetDataAsync();
        using HttpClient admin = await factory.CreateAdminClientAsync();
        _ = await admin.PostAsJsonAsync("/api/spoolman/code-aliases", new { code = Fnsku, filamentId = 42 });

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(LookupUrl("012345678905"))).StatusCode);
        Assert.Equal(ScanCodeAliasLookupState.NotRemembered, (await LookupAsync(admin, "00224")).State);
    }

    [Fact]
    public async Task Http_UnknownFilament_Returns404AndStoresNothing()
    {
        Mock<ISpoolmanService> spoolman = SpoolmanWith(CreateFilament(42));
        await using AliasFactory factory = new(spoolman);
        await factory.ResetDataAsync();
        using HttpClient admin = await factory.CreateAdminClientAsync();

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/spoolman/code-aliases", new { code = Fnsku, filamentId = 99 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ScanCodeAliasLookupState.NotRemembered, (await LookupAsync(admin, Fnsku)).State);
    }

    [Fact]
    public async Task Http_SpoolmanUnavailable_Returns502ForSaveAndLookup()
    {
        Mock<ISpoolmanService> spoolman = SpoolmanWith(CreateFilament(42));
        await using AliasFactory factory = new(spoolman);
        await factory.ResetDataAsync();
        using HttpClient admin = await factory.CreateAdminClientAsync();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/spoolman/code-aliases", new { code = Fnsku, filamentId = 42 })).StatusCode);

        spoolman.Setup(s => s.FindFilamentByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SpoolmanApiException("down"));

        Assert.Equal(HttpStatusCode.BadGateway, (await admin.GetAsync(LookupUrl(Fnsku))).StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, (await admin.PostAsJsonAsync("/api/spoolman/code-aliases", new { code = "B08XYZ1234", filamentId = 42 })).StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.ScanCodeAliases.CountAsync());
    }

    [Fact]
    public async Task Http_Unauthenticated_Returns401()
    {
        Mock<ISpoolmanService> spoolman = SpoolmanWith(CreateFilament(42));
        await using AliasFactory factory = new(spoolman);
        using HttpClient client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(LookupUrl(Fnsku))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/spoolman/code-aliases", new { code = Fnsku, filamentId = 42 })).StatusCode);
    }

    [Fact]
    public async Task Http_NonAdminCanLookUpButCannotSave()
    {
        Mock<ISpoolmanService> spoolman = SpoolmanWith(CreateFilament(42));
        await using AliasFactory factory = new(spoolman);
        await factory.ResetDataAsync();
        using HttpClient user = await factory.CreateAuthenticatedClientAsync("scan-user", "scan-user@example.com");

        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/spoolman/code-aliases", new { code = Fnsku, filamentId = 42 })).StatusCode);
        Assert.Equal(ScanCodeAliasLookupState.NotRemembered, (await LookupAsync(user, Fnsku)).State);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.ScanCodeAliases.CountAsync());
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static async Task<ScanCodeAliasLookupResponse> LookupAsync(HttpClient client, string code)
    {
        HttpResponseMessage response = await client.GetAsync(LookupUrl(code));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ScanCodeAliasLookupResponse>(JsonOptions))!;
    }

    private static string LookupUrl(string code) => $"/api/spoolman/code-aliases/filament?code={Uri.EscapeDataString(code)}";

    private static Mock<ISpoolmanService> SpoolmanWith(params SpoolmanFilamentDto[] filaments)
    {
        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(s => s.FindFilamentByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => filaments.FirstOrDefault(f => f.Id == id));
        return spoolman;
    }

    private static SpoolmanFilamentDto CreateFilament(int id)
        => new(
            Id: id,
            Name: $"Filament {id}",
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
            ArticleNumber: null,
            Comment: null,
            MultiColorHexes: null,
            Gtin: null,
            ExternalId: null);

    /// <summary>File-backed SQLite database migrated with the shipped SQLite migrations.</summary>
    private sealed class TempSqliteDatabase : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"scan-alias-{Guid.NewGuid():N}.db");

        private string ConnectionString => $"Data Source={path};Pooling=False";

        public static async Task<TempSqliteDatabase> CreateAsync()
        {
            TempSqliteDatabase database = new();
            await using AppDbContext db = database.CreateContext();
            await db.Database.MigrateAsync();
            return database;
        }

        public AppDbContext CreateContext()
            => new(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(ConnectionString, sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite"))
                .Options);

        public ScanCodeAliasService CreateService(Mock<ISpoolmanService> spoolman)
        {
            Mock<IDbContextFactory<AppDbContext>> factory = new();
            factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(CreateContext()));
            return new ScanCodeAliasService(factory.Object, spoolman.Object, NullLogger<ScanCodeAliasService>.Instance);
        }

        public async Task<int> CountAsync()
        {
            await using AppDbContext db = CreateContext();
            return await db.ScanCodeAliases.CountAsync();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
