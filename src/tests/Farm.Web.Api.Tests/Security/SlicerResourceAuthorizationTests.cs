using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

public sealed class SlicerResourceAuthorizationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
            ["Security:DevModeBypassAuth"] = "false",
        });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task GetSliceJobAsync_WithoutQueueReadPermission_ReturnsPermissionDenied()
    {
        Guid userId = Guid.NewGuid();
        SliceJob job = await AddJobAsync(userId);
        using HttpClient client = CreateUserClient(userId);

        HttpResponseMessage response = await client.GetAsync($"/api/slice/{job.Id}");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("permission_denied");
    }

    [Fact]
    public async Task GetSliceJobAsync_ForDifferentOwner_ReturnsResourceForbidden()
    {
        SliceJob job = await AddJobAsync(Guid.NewGuid());
        using HttpClient client = CreateUserClient(
            Guid.NewGuid(),
            PrintFarmerPermissions.Queue.Read);

        HttpResponseMessage response = await client.GetAsync($"/api/slice/{job.Id}");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("resource_forbidden");
    }

    [Fact]
    public async Task GetSliceJobAsync_ForOwner_ReturnsRedactedStatus()
    {
        Guid userId = Guid.NewGuid();
        SliceJob job = await AddJobAsync(
            userId,
            errorMessage: "private worker filesystem exception",
            resultFileUrl: @"D:\private\artifacts\result.gcode");
        using HttpClient client = CreateUserClient(userId, PrintFarmerPermissions.Queue.Read);

        HttpResponseMessage response = await client.GetAsync($"/api/slice/{job.Id}");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        _ = root.GetProperty("id").GetGuid().Should().Be(job.Id);
        _ = root.GetProperty("errorMessage").GetString().Should().Be("Slicing failed.");
        _ = root.GetProperty("artifactsRoute").GetString()
            .Should().Be($"/api/artifacts/job/{job.Id}");
        _ = body.Should().NotContain("private worker");
        _ = body.Should().NotContain(@"D:\private");
        _ = body.Should().NotContain("modelFileUrl");
        _ = body.Should().NotContain("slicerProfileJson");
        _ = body.Should().NotContain("userId");
        _ = body.Should().NotContain("workerId");
    }

    [Fact]
    public async Task ListSliceJobsAsync_ForUser_ReturnsOnlyOwnedJobs()
    {
        Guid userId = Guid.NewGuid();
        SliceJob ownedJob = await AddJobAsync(userId);
        SliceJob foreignJob = await AddJobAsync(Guid.NewGuid());
        using HttpClient client = CreateUserClient(userId, PrintFarmerPermissions.Queue.Read);

        HttpResponseMessage response = await client.GetAsync("/api/slice");
        JsonDocument document = await response.Content.ReadFromJsonAsync<JsonDocument>()
            ?? throw new InvalidOperationException("Missing job list response.");

        using (document)
        {
            _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
            Guid[] returnedIds = document.RootElement.EnumerateArray()
                .Select(job => job.GetProperty("id").GetGuid())
                .ToArray();
            _ = returnedIds.Should().Contain(ownedJob.Id);
            _ = returnedIds.Should().NotContain(foreignJob.Id);
        }
    }

    [Fact]
    public async Task GetSliceJobAsync_ForFarmAdministrator_AllowsAuditedOwnerBypass()
    {
        SliceJob job = await AddJobAsync(Guid.NewGuid());
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/api/slice/{job.Id}");

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SubmitSliceJobAsync_WithSpoofedUserId_PersistsAuthenticatedOwner()
    {
        Guid authenticatedUserId = Guid.NewGuid();
        using HttpClient client = CreateUserClient(
            authenticatedUserId,
            PrintFarmerPermissions.Slicing.Submit);
        SubmitSliceJobRequest request = new()
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "file:///private/models/input.stl",
            ModelFileName = "input.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = "{\"private\":true}",
        };

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/slice", request);
        SubmitSliceJobResponse result =
            await response.Content.ReadFromJsonAsync<SubmitSliceJobResponse>()
            ?? throw new InvalidOperationException("Missing submit response.");

        _ = response.StatusCode.Should().Be(HttpStatusCode.Created);
        using IServiceScope scope = _factory.Services.CreateScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob saved = await db.SliceJobs.AsNoTracking().SingleAsync(job => job.Id == result.JobId);
        _ = saved.UserId.Should().Be(authenticatedUserId);
    }

    [Fact]
    public async Task SubmitSliceJobAsync_ForDisabledPrinter_ReturnsResourceForbidden()
    {
        Guid printerId = await AddPrinterAsync(isEnabled: false);
        Guid userId = Guid.NewGuid();
        using HttpClient client = CreateUserClient(userId, PrintFarmerPermissions.Slicing.Submit);
        SubmitSliceJobRequest request = new()
        {
            UserId = userId,
            PrinterId = printerId,
            ModelFileUrl = "file:///models/input.stl",
            ModelFileName = "input.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        };

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/slice", request);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("resource_forbidden");
    }

    [Fact]
    public async Task GetArtifactMetadataAsync_ForDifferentOwner_ReturnsResourceForbidden()
    {
        SliceJob job = await AddJobAsync(Guid.NewGuid());
        Artifact artifact = await AddArtifactAsync(job.Id);
        using HttpClient client = CreateUserClient(
            Guid.NewGuid(),
            PrintFarmerPermissions.Slicing.ReadArtifact);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/artifacts/{artifact.Id}/metadata");

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetArtifactMetadataAsync_ForOwner_ReturnsRedactedMetadata()
    {
        Guid userId = Guid.NewGuid();
        SliceJob job = await AddJobAsync(userId);
        Artifact artifact = await AddArtifactAsync(job.Id);
        using HttpClient client = CreateUserClient(
            userId,
            PrintFarmerPermissions.Slicing.ReadArtifact);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/artifacts/{artifact.Id}/metadata");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        _ = body.Should().NotContain("relativePath");
        _ = body.Should().NotContain("sha256");
        _ = body.Should().NotContain("private-artifacts");
        _ = body.Should().NotContain("secret-hash");
    }

    private HttpClient CreateUserClient(Guid userId, params string[] permissions)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", userId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "operator");
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    private async Task<SliceJob> AddJobAsync(
        Guid userId,
        string? errorMessage = null,
        string? resultFileUrl = null)
    {
        SliceJob job = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ModelFileUrl = "file:///private/models/model.stl",
            ModelFileName = "model.stl",
            SlicerEngine = (int)SlicerEngineType.OrcaSlicer,
            SlicerEngineName = SlicerEngineType.OrcaSlicer.ToString(),
            SlicerProfileJson = "{\"secret\":\"profile\"}",
            Status = SliceJobStatus.Failed,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ErrorMessage = errorMessage,
            ResultFileUrl = resultFileUrl,
        };

        using IServiceScope scope = _factory.Services.CreateScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        _ = db.SliceJobs.Add(job);
        _ = await db.SaveChangesAsync();
        return job;
    }

    private async Task<Artifact> AddArtifactAsync(Guid jobId)
    {
        Artifact artifact = new()
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            Kind = "gcode",
            FileName = "result.gcode",
            RelativePath = "private-artifacts/result.gcode",
            ContentType = "text/x.gcode",
            SizeBytes = 128,
            Sha256 = "secret-hash",
            CreatedAt = DateTime.UtcNow,
        };

        using IServiceScope scope = _factory.Services.CreateScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        _ = db.Artifacts.Add(artifact);
        _ = await db.SaveChangesAsync();
        return artifact;
    }

    private async Task<Guid> AddPrinterAsync(bool isEnabled)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Manufacturer? manufacturer = await db.Manufacturers.FirstOrDefaultAsync();
        if (manufacturer is null)
        {
            manufacturer = new Manufacturer
            {
                Id = Guid.NewGuid(),
                Name = "Authorization test manufacturer",
            };
            _ = db.Manufacturers.Add(manufacturer);
            _ = await db.SaveChangesAsync();
        }

        PrinterModel? model = await db.PrinterModels.FirstOrDefaultAsync();
        if (model is null)
        {
            model = new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "Authorization test model",
                ManufacturerId = manufacturer.Id,
            };
            _ = db.PrinterModels.Add(model);
            _ = await db.SaveChangesAsync();
        }

        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Authorization test printer",
            Backend = (int)PrinterBackend.Moonraker,
            ServerUrl = "http://private-printer.local",
            BackendPort = 7125,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            IsEnabled = isEnabled,
        };

        _ = db.Printers.Add(printer);
        _ = await db.SaveChangesAsync();
        return printer.Id;
    }
}
