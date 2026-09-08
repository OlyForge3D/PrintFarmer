using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Contracts.FileManagement;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Quota;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Modules.Gcode.Controllers;
using Farm.Modules.Gcode.DTOs;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Modules.Gcode.Tests.Controllers;

/// <summary>
/// Exercises the real HTTP authorization boundary and settings adapter for issue #2534.
/// </summary>
public sealed class GcodeFilesControllerSettingsTests : IAsyncLifetime, IDisposable
{
    private const string SettingsRoute = "/api/gcode-files/settings";
    private readonly Mock<ISettingsService> _settingsService = new();
    private readonly GcodeUploadSettings _settings = new() { AllowedExtensions = [".gcode"] };
    private readonly IHost _host;

    public GcodeFilesControllerSettingsTests()
    {
        _settingsService.Setup(service => service.Get<GcodeUploadSettings>()).Returns(_settings);
        var uploadSettings = new PersistedGcodeUploadSettingsAdapter(_settingsService.Object);
        var filesService = new Mock<IGcodeFilesService>();
        filesService.Setup(service => service.GetSettingsAsync(
                It.IsAny<string>(), It.IsAny<IGcodeUploadSettings>(),
                It.IsAny<IGcodeUploadQuotaService>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GcodeUploadSettingsResponse(uploadSettings.GetAllowedExtensions(), 1024, 0));

        _host = new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, SettingsTestAuthHandler>("Test", _ => { });
                services.AddAuthorization();
                services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
                services.AddControllers().AddApplicationPart(typeof(GcodeFilesController).Assembly);
                services.AddSingleton<IGcodeUploadSettings>(uploadSettings);
                services.AddSingleton(filesService.Object);
                services.AddSingleton(Mock.Of<IGcodeUploadQuotaService>());
                services.AddSingleton(Mock.Of<IChunkedUploadService>());
                services.AddSingleton(Mock.Of<IFileManagementService>());
                services.AddSingleton(Mock.Of<IStoragePathService>());
                services.AddSingleton(Mock.Of<IStoredFileOperationsService>());
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            })).Build();
    }

    [Fact]
    public async Task UpdateSettings_AnonymousCaller_Returns401WithoutChangingSettings()
    {
        using HttpClient client = _host.GetTestClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            SettingsRoute, new { allowedExtensions = new[] { ".bgcode" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertSettingsUnchanged();
    }

    [Theory]
    [InlineData("")]
    [InlineData("system_settings:read")]
    [InlineData("system_settings:update")]
    [InlineData("gcode_library:admin")]
    public async Task UpdateSettings_WithoutSystemSettingsAdmin_Returns403WithoutChangingSettings(string permission)
    {
        using HttpClient client = CreateClient(permission);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            SettingsRoute, new { allowedExtensions = new[] { ".bgcode" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertSettingsUnchanged();
    }

    [Theory]
    [InlineData("system_settings:admin", "operator")]
    [InlineData("", PrintFarmerPermissions.FarmAdminRole)]
    public async Task UpdateSettings_AuthorizedCaller_Returns204AndPersistsNormalizedExtensions(
        string permission, string role)
    {
        using HttpClient client = CreateClient(permission, role);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            SettingsRoute, new { allowedExtensions = new[] { "GCODE", ".BGCODE", ".gcode" } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new[] { ".gcode", ".bgcode" }, _settings.AllowedExtensions);
        _settingsService.Verify(service => service.Save(It.Is<GcodeUploadSettings>(
            settings => settings.AllowedExtensions.SequenceEqual(new[] { ".gcode", ".bgcode" }))), Times.Once);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"allowedExtensions\":null}")]
    [InlineData("{\"allowedExtensions\":[]}")]
    [InlineData("null")]
    [InlineData("{")]
    public async Task UpdateSettings_AuthorizedCallerWithInvalidBody_Returns400WithoutChangingSettings(string body)
    {
        using HttpClient client = CreateClient("system_settings:admin");
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PutAsync(SettingsRoute, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertSettingsUnchanged();
    }

    [Fact]
    public async Task UpdateSettings_UnauthorizedCallerWithInvalidBody_Returns403BeforeValidation()
    {
        using HttpClient client = CreateClient();
        using var content = new StringContent("{", Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PutAsync(SettingsRoute, content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertSettingsUnchanged();
    }

    [Fact]
    public async Task GetSettings_AuthenticatedCallerWithoutAdmin_ReturnsSettings()
    {
        using HttpClient client = CreateClient();

        using HttpResponseMessage response = await client.GetAsync(SettingsRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GcodeUploadSettingsResponse? settings = await response.Content.ReadFromJsonAsync<GcodeUploadSettingsResponse>();
        Assert.NotNull(settings);
        Assert.Equal(new[] { ".gcode" }, settings.AllowedExtensions);
        AssertSettingsUnchanged();
    }

    private HttpClient CreateClient(string permission = "", string role = "operator")
    {
        HttpClient client = _host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        if (!string.IsNullOrEmpty(permission))
        {
            client.DefaultRequestHeaders.Add("X-Test-Permission", permission);
        }

        return client;
    }

    private void AssertSettingsUnchanged()
    {
        Assert.Equal(new[] { ".gcode" }, _settings.AllowedExtensions);
        _settingsService.Verify(service => service.Save(It.IsAny<GcodeUploadSettings>()), Times.Never);
    }

    public Task InitializeAsync() => _host.StartAsync();

    public Task DisposeAsync() => _host.StopAsync();

    public void Dispose() => _host.Dispose();

    private sealed class SettingsTestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string role = Request.Headers["X-Test-Role"].ToString();
            if (string.IsNullOrEmpty(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            List<Claim> claims =
            [
                new(ClaimTypes.NameIdentifier, "gcode-settings-test-user"),
                new(ClaimTypes.Role, role),
            ];
            string permission = Request.Headers["X-Test-Permission"].ToString();
            if (!string.IsNullOrEmpty(permission))
            {
                claims.Add(new Claim(PrintFarmerPermissions.ClaimType, permission));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
