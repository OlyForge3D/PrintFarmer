using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Contracts.FileManagement;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Quota;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Modules.Gcode.Controllers;
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
/// HTTP wire contract for the G-code delete routes (issue #3579): <c>{ fileIds }</c> request body,
/// additive typed per-ID results beside the existing counts, single-delete status mapping, and the
/// unchanged <c>gcode_library:admin</c> authorization boundary.
/// </summary>
public sealed class GcodeFilesControllerDeleteTests : IAsyncLifetime, IDisposable
{
    private const string Route = "/api/gcode-files";
    private readonly Mock<IGcodeFilesService> _filesService = new();
    private readonly List<IReadOnlyList<Guid>> _calls = [];
    private readonly IHost _host;
    private IReadOnlyList<BulkDeleteItemResult> _nextResults = [];

    public GcodeFilesControllerDeleteTests()
    {
        _filesService.Setup(s => s.DeleteFilesAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Guid> ids, CancellationToken _) =>
            {
                _calls.Add(ids.ToList());
                return _nextResults;
            });

        _host = new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, DeleteTestAuthHandler>("Test", _ => { });
                services.AddAuthorization();
                services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
                services.AddControllers().AddApplicationPart(typeof(GcodeFilesController).Assembly);
                services.AddSingleton(Mock.Of<IGcodeUploadSettings>());
                services.AddSingleton(_filesService.Object);
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
    public async Task BulkDelete_PassesFileIdsAndReturnsCountsWithTypedResults()
    {
        Guid deleted = Guid.NewGuid();
        Guid cleanup = Guid.NewGuid();
        Guid active = Guid.NewGuid();
        Guid missing = Guid.NewGuid();
        Guid unknown = Guid.NewGuid();
        _nextResults =
        [
            new(deleted, BulkDeleteItemStatus.Deleted),
            new(cleanup, BulkDeleteItemStatus.DeletedCleanupIncomplete, BulkDeleteItemReasons.FileCleanupFailed),
            new(active, BulkDeleteItemStatus.Blocked, BulkDeleteItemReasons.ActiveJobs),
            new(missing, BulkDeleteItemStatus.NotFound),
            new(unknown, BulkDeleteItemStatus.Unknown, BulkDeleteItemReasons.CommitOutcomeUnknown),
        ];
        using HttpClient client = CreateClient("gcode_library:admin");

        using HttpResponseMessage response = await SendBulkAsync(
            client, $"{{\"fileIds\":[\"{deleted}\",\"{cleanup}\",\"{active}\",\"{missing}\",\"{unknown}\",\"{deleted}\"]}}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        IReadOnlyList<Guid> call = Assert.Single(_calls);
        Assert.Equal(new[] { deleted, cleanup, active, missing, unknown, deleted }, call);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(new[] { "deleted", "totalRequested", "results" }, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, root.GetProperty("deleted").GetInt32());
        Assert.Equal(5, root.GetProperty("totalRequested").GetInt32());
        JsonElement[] results = root.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(
            new (Guid, string?, string?)[]
            {
                (deleted, "Deleted", null),
                (cleanup, "DeletedCleanupIncomplete", "FileCleanupFailed"),
                (active, "Blocked", "ActiveJobs"),
                (missing, "NotFound", null),
                (unknown, "Unknown", "CommitOutcomeUnknown"),
            },
            results.Select(r => (
                r.GetProperty("id").GetGuid(),
                r.GetProperty("status").GetString(),
                r.TryGetProperty("reason", out JsonElement reason) ? reason.GetString() : null)));
        Assert.All(results, r => Assert.DoesNotContain(r.EnumerateObject(), p => p.Name == "recordRemoved"));
    }

    [Theory]
    [InlineData("{\"fileIds\":[]}")]
    [InlineData("{}")]
    public async Task BulkDelete_EmptyFileIds_Returns400WithoutCallingService(string body)
    {
        using HttpClient client = CreateClient("gcode_library:admin");

        using HttpResponseMessage response = await SendBulkAsync(client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task BulkDelete_ModelIdsBodyShape_IsNotAcceptedAsFileIds()
    {
        using HttpClient client = CreateClient("gcode_library:admin");

        using HttpResponseMessage response = await SendBulkAsync(client, $"{{\"ids\":[\"{Guid.NewGuid()}\"]}}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("gcode_library:read")]
    [InlineData("slicer_engines:admin")]
    public async Task DeleteRoutes_WithoutGcodeLibraryAdmin_Return403WithoutCallingService(string permission)
    {
        using HttpClient client = CreateClient(permission);

        using HttpResponseMessage bulk = await SendBulkAsync(client, $"{{\"fileIds\":[\"{Guid.NewGuid()}\"]}}");
        using HttpResponseMessage single = await client.DeleteAsync($"{Route}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, bulk.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, single.StatusCode);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task DeleteRoutes_Anonymous_Return401()
    {
        using HttpClient client = _host.GetTestClient();

        using HttpResponseMessage bulk = await SendBulkAsync(client, $"{{\"fileIds\":[\"{Guid.NewGuid()}\"]}}");
        using HttpResponseMessage single = await client.DeleteAsync($"{Route}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, bulk.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, single.StatusCode);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(BulkDeleteItemStatus.Deleted, null, HttpStatusCode.NoContent)]
    [InlineData(BulkDeleteItemStatus.DeletedCleanupIncomplete, BulkDeleteItemReasons.ThumbnailCleanupFailed, HttpStatusCode.NoContent)]
    [InlineData(BulkDeleteItemStatus.Blocked, BulkDeleteItemReasons.ActiveJobs, HttpStatusCode.Conflict)]
    [InlineData(BulkDeleteItemStatus.Blocked, BulkDeleteItemReasons.UnsafePath, HttpStatusCode.NotFound)]
    [InlineData(BulkDeleteItemStatus.NotFound, null, HttpStatusCode.NotFound)]
    [InlineData(BulkDeleteItemStatus.Failed, BulkDeleteItemReasons.DatabaseFailed, HttpStatusCode.InternalServerError)]
    [InlineData(BulkDeleteItemStatus.Unknown, BulkDeleteItemReasons.CommitOutcomeUnknown, HttpStatusCode.InternalServerError)]
    public async Task SingleDelete_MapsItemOutcomeToStatusCode(
        BulkDeleteItemStatus status, string? reason, HttpStatusCode expected)
    {
        Guid id = Guid.NewGuid();
        _nextResults = [new(id, status, reason)];
        using HttpClient client = CreateClient("gcode_library:admin");

        using HttpResponseMessage response = await client.DeleteAsync($"{Route}/{id}");

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(new[] { id }, Assert.Single(_calls));
        if (expected == HttpStatusCode.Conflict)
        {
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(id, body.RootElement.GetProperty("fileId").GetGuid());
            Assert.Contains("active job", body.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
    }

    private static Task<HttpResponseMessage> SendBulkAsync(HttpClient client, string json) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, Route)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    private HttpClient CreateClient(string permission)
    {
        HttpClient client = _host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        if (!string.IsNullOrEmpty(permission))
        {
            client.DefaultRequestHeaders.Add("X-Test-Permission", permission);
        }

        return client;
    }

    public Task InitializeAsync() => _host.StartAsync();

    public Task DisposeAsync() => _host.StopAsync();

    public void Dispose() => _host.Dispose();

    private sealed class DeleteTestAuthHandler(
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
                new(ClaimTypes.NameIdentifier, "gcode-delete-test-user"),
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
