extern alias SlicerHost;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicers.OrcaSlicer.v2_4_0;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SlicerHostProgram = SlicerHost::Program;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// Verifies model-byte security through the standalone slicer-host pipeline used by split deployments.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class StandaloneSlicerHostModelDownloadSecurityTests(
    StandaloneSlicerHostApplicationFactory factory)
    : IClassFixture<StandaloneSlicerHostApplicationFactory>
{
    [Theory]
    [InlineData("/api/3d-models/file/00000000-0000-0000-0000-000000000000")]
    [InlineData("/api/3d-models/download-for-viewer?path=missing.stl")]
    public async Task ModelByteEndpoint_WithoutAuthentication_Returns401(string path)
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetModelFile_WithAuthenticatedRequest_Returns200()
    {
        Guid modelId = Guid.NewGuid();
        string fileName = $"{modelId}.stl";
        string filePath = Path.Combine(factory.ModelStoragePath, fileName);
        await File.WriteAllTextAsync(filePath, "split-host model file");

        try
        {
            using HttpClient client = factory.CreateAuthenticatedClient();
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
            SlicerDbContext dbContext = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            dbContext.Models3D.Add(new Model3D
            {
                Id = modelId,
                Name = "split-host-model.stl",
                FileName = fileName,
                FilePath = factory.ModelStoragePath,
                FileSizeBytes = new FileInfo(filePath).Length,
                FileHash = $"hash-{modelId}",
                FileFormat = ModelFileFormat.STL,
                IsValid = true,
                FolderId = Guid.NewGuid(),
                UploadedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = await dbContext.SaveChangesAsync();

            HttpResponseMessage response = await client.GetAsync(
                $"/api/3d-models/file/{modelId}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Be("split-host model file");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithAuthenticatedRequest_Returns200()
    {
        string fileName = $"viewer-{Guid.NewGuid():N}.stl";
        string filePath = Path.Combine(factory.ModelStoragePath, fileName);
        await File.WriteAllTextAsync(filePath, "split-host viewer");

        try
        {
            using HttpClient client = factory.CreateAuthenticatedClient();
            HttpResponseMessage response = await client.GetAsync(BuildViewerDownloadUrl(fileName));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Be("split-host viewer");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithTraversalPath_Returns400()
    {
        string outsidePath = CreateOutsideFile("traversal");
        string traversalPath = Path.GetRelativePath(factory.ModelStoragePath, outsidePath);

        try
        {
            using HttpClient client = factory.CreateAuthenticatedClient();
            HttpResponseMessage response = await client.GetAsync(
                BuildViewerDownloadUrl(traversalPath));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithAbsolutePath_Returns400()
    {
        string outsidePath = CreateOutsideFile("absolute");

        try
        {
            using HttpClient client = factory.CreateAuthenticatedClient();
            HttpResponseMessage response = await client.GetAsync(
                BuildViewerDownloadUrl(outsidePath));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithSymlinkOutsideStorageRoot_Returns403()
    {
        string outsidePath = CreateOutsideFile("symlink");
        string linkPath = Path.Combine(
            factory.ModelStoragePath,
            $"link-{Guid.NewGuid():N}.stl");
        _ = File.CreateSymbolicLink(linkPath, outsidePath);

        try
        {
            using HttpClient client = factory.CreateAuthenticatedClient();
            HttpResponseMessage response = await client.GetAsync(
                BuildViewerDownloadUrl(Path.GetFileName(linkPath)));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            File.Delete(linkPath);
            File.Delete(outsidePath);
        }
    }

    private string CreateOutsideFile(string prefix)
    {
        string path = Path.Combine(
            factory.OutsideStoragePath,
            $"{prefix}-{Guid.NewGuid():N}.stl");
        File.WriteAllText(path, "outside");
        return path;
    }

    private static string BuildViewerDownloadUrl(string path)
    {
        return $"/api/3d-models/download-for-viewer?path={Uri.EscapeDataString(path)}";
    }
}

/// <summary>
/// Hosts the production standalone slicer entry point with deterministic test storage and JWT settings.
/// </summary>
public sealed class StandaloneSlicerHostApplicationFactory : WebApplicationFactory<SlicerHostProgram>
{
    private const string JwtKey =
        "PrintFarmerStandaloneSlicerHostModelDownloadSecurityTestsKey-1234567890";
    private const string JwtIssuer = "PrintFarmer";
    private const string JwtAudience = "PrintFarmer";

    private readonly string _testRoot;
    private readonly string _databasePath;

    public StandaloneSlicerHostApplicationFactory()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            $"slicer-host-security-{Guid.NewGuid():N}");
        ModelStoragePath = Path.Combine(_testRoot, "models");
        OutsideStoragePath = Path.Combine(
            Path.GetTempPath(),
            $"slicer-host-security-outside-{Guid.NewGuid():N}");
        _databasePath = Path.Combine(_testRoot, "slicer-host.db");
        Directory.CreateDirectory(ModelStoragePath);
        Directory.CreateDirectory(OutsideStoragePath);

        _ = typeof(OrcaSlicerLibrary_v2_4_0).Assembly.GetName();
    }

    public string ModelStoragePath { get; }

    public string OutsideStoragePath { get; }

    public HttpClient CreateAuthenticatedClient()
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "split-host-test-user"),
            ]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Issuer = JwtIssuer,
            Audience = JwtAudience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)),
                SecurityAlgorithms.HmacSha256),
        };

        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JsonWebTokenHandler().CreateToken(descriptor));
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _ = builder.UseEnvironment("Testing");
        _ = builder.UseSetting("Jwt:Key", JwtKey);
        _ = builder.UseSetting("Jwt:Issuer", JwtIssuer);
        _ = builder.UseSetting("Jwt:Audience", JwtAudience);
        _ = builder.UseSetting("DB_PROVIDER", "sqlite");
        _ = builder.UseSetting(
            "ConnectionStrings:Default",
            $"Data Source={_databasePath};Pooling=False");
        _ = builder.UseSetting("STORAGE_PATHS:UPLOADS", ModelStoragePath);
        _ = builder.UseSetting(
            "STORAGE_PATHS:GCODE",
            Path.Combine(_testRoot, "gcode"));
        _ = builder.UseSetting("WorkerAuth:SharedKey", "split-host-test-worker-key");
        _ = builder.UseSetting(
            "ArtifactStorage:RootPath",
            Path.Combine(_testRoot, "artifacts"));
        _ = builder.UseSetting("ArtifactStorage:EnableStorageAlerts", "false");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
        if (Directory.Exists(OutsideStoragePath))
        {
            Directory.Delete(OutsideStoragePath, recursive: true);
        }
    }
}
