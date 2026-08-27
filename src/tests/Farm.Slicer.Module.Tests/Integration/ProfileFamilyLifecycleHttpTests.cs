using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// HTTP-level coverage for the profile-family lifecycle list/get/delete endpoints (issue #2079,
/// Phase 4 slice 1). These tests drive the real controller, service, alias, and catalog services
/// but substitute a recording <see cref="IProfileFamilyWorkerClient"/> so the worker bundle round
/// trip is observable and its failure can be injected deterministically. The end-to-end proof that
/// deletion removes the worker bundle and stops the model resolving without a worker restart lives
/// in <c>ProfileFamilyEndToEndHttpTests</c> against a real hosted worker.
/// </summary>
public sealed class ProfileFamilyLifecycleHttpTests
{
    [Fact]
    public async Task ListFamilies_ReturnsCreatedFamilies_WithCamelCaseStringEnums()
    {
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();
        Guid modelId = Guid.NewGuid();
        _ = await SeedFamilyAsync(factory, "Farm List One", modelId);

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-list-admin", "profile-family-list@example.com");

        using HttpResponseMessage response =
            await client.GetAsync("/api/slicer/profiles/families");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement only = body.RootElement.EnumerateArray().Single();
        only.GetProperty("familyName").GetString().Should().Be("Farm List One");
        only.GetProperty("renderStatus").GetString().Should().Be("Healthy");
        only.GetProperty("targetPrinterModelId").GetString().Should().Be(modelId.ToString());
        // sourceManufacturer / process- and filament-profile counts are deliberately null; the API
        // serializer omits null members, so the honest contract is "absent, or present-and-null" —
        // never a fabricated value. Assert they are not a real, non-null value.
        AssertNullOrAbsent(only, "sourceManufacturer");
        AssertNullOrAbsent(only, "processProfileCount");
        AssertNullOrAbsent(only, "filamentProfileCount");
        JsonElement variant = only.GetProperty("variants").EnumerateArray().Single();
        variant.GetProperty("nozzleDiameter").GetDouble().Should().Be(0.4);
    }

    private static void AssertNullOrAbsent(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out JsonElement value))
        {
            value.ValueKind.Should().Be(
                JsonValueKind.Null,
                $"{propertyName} must never carry a fabricated value; it is null (or omitted).");
        }
    }

    [Fact]
    public async Task ListFamilies_NonAdminWithSlicingSubmit_Succeeds()
    {
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();
        _ = await SeedFamilyAsync(factory, "Farm Reader Family", Guid.NewGuid());

        using HttpClient client = await factory.CreateOperatorClientAsync(
            "slicing", "submit", username: "profile-family-reader");

        using HttpResponseMessage response =
            await client.GetAsync("/api/slicer/profiles/families");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "reading families is gated on slicing:submit, not the admin gate that guards creation");
        List<ProfileFamilySummaryDto>? families =
            await response.Content.ReadFromJsonAsync<List<ProfileFamilySummaryDto>>();
        families.Should().ContainSingle(family => family.FamilyName == "Farm Reader Family");
    }

    [Fact]
    public async Task ListFamilies_FiltersByRenderStatus()
    {
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();
        _ = await SeedFamilyAsync(factory, "Healthy Family", Guid.NewGuid());
        _ = await SeedFamilyAsync(
            factory, "Failed Family", Guid.NewGuid(), ProfileFamilyRenderStatus.Failed);

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-filter-admin", "profile-family-filter@example.com");

        using HttpResponseMessage response =
            await client.GetAsync("/api/slicer/profiles/families?renderStatus=Failed");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<ProfileFamilySummaryDto>? families =
            await response.Content.ReadFromJsonAsync<List<ProfileFamilySummaryDto>>();
        _ = families.Should().ContainSingle();
        families![0].FamilyName.Should().Be("Failed Family");
    }

    [Fact]
    public async Task ListFamilies_InvalidRenderStatus_ReturnsFeatureErrorEnvelope()
    {
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-badstatus-admin", "profile-family-badstatus@example.com");

        using HttpResponseMessage response =
            await client.GetAsync("/api/slicer/profiles/families?renderStatus=Nonsense");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("invalid_render_status");
        body.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.TryGetProperty("errors", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ListFamilies_NumericRenderStatus_ReturnsFeatureErrorEnvelope()
    {
        // S1: the API contract is string enum names only (JsonStringEnumConverter). A numeric value like
        // "2" must be rejected, not silently accepted by Enum.TryParse's numeric-literal support.
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-numstatus-admin", "profile-family-numstatus@example.com");

        using HttpResponseMessage response =
            await client.GetAsync("/api/slicer/profiles/families?renderStatus=2");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("invalid_render_status");
        body.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.TryGetProperty("errors", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetFamily_UnknownId_Returns404FeatureErrorEnvelope()
    {
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-get404-admin", "profile-family-get404@example.com");

        using HttpResponseMessage response =
            await client.GetAsync($"/api/slicer/profiles/families/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("profile_family_not_found");
        body.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.TryGetProperty("errors", out _).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteFamily_RemovesRowsAndCallsWorkerBundleDelete()
    {
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = await SeedFamilyAsync(factory, "Farm Delete Family", modelId);

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-delete-admin", "profile-family-delete@example.com");

        using HttpResponseMessage delete =
            await client.DeleteAsync($"/api/slicer/profiles/families/{familyId}");

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        worker.DeletedFamilyIds.Should().Equal(familyId);

        using HttpResponseMessage getAfter =
            await client.GetAsync($"/api/slicer/profiles/families/{familyId}");
        getAfter.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using HttpResponseMessage listAfter =
            await client.GetAsync("/api/slicer/profiles/families");
        List<ProfileFamilySummaryDto>? families =
            await listAfter.Content.ReadFromJsonAsync<List<ProfileFamilySummaryDto>>();
        families.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFamily_UnknownId_Returns404_WithoutWorkerCall()
    {
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-delete404-admin", "profile-family-delete404@example.com");

        using HttpResponseMessage delete =
            await client.DeleteAsync($"/api/slicer/profiles/families/{Guid.NewGuid()}");

        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using JsonDocument body = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("profile_family_not_found");
        worker.DeletedFamilyIds.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFamily_NonTerminalSliceJobReference_Returns409NamingJob()
    {
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();
        Guid modelId = Guid.NewGuid();
        (Guid familyId, Guid variantId) = await SeedFamilyAsync(factory, "Farm Job Family", modelId);
        Guid jobId = await SeedSliceJobAsync(factory, variantId, SliceJobStatus.Queued);

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-job-admin", "profile-family-job@example.com");

        using HttpResponseMessage delete =
            await client.DeleteAsync($"/api/slicer/profiles/families/{familyId}");

        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using JsonDocument body = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("profile_family_in_use");
        body.RootElement.GetProperty("detail").GetString().Should().Contain(jobId.ToString());
        worker.DeletedFamilyIds.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFamily_TerminalSliceJobReference_Succeeds()
    {
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();
        Guid modelId = Guid.NewGuid();
        (Guid familyId, Guid variantId) =
            await SeedFamilyAsync(factory, "Farm Completed Job Family", modelId);
        _ = await SeedSliceJobAsync(factory, variantId, SliceJobStatus.Completed);

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-completed-admin", "profile-family-completed@example.com");

        using HttpResponseMessage delete =
            await client.DeleteAsync($"/api/slicer/profiles/families/{familyId}");

        delete.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            "a terminal job persists its own machine-profile snapshot, so its provenance survives deletion");
        worker.DeletedFamilyIds.Should().Equal(familyId);
    }

    [Fact]
    public async Task DeleteFamily_PrinterReference_Returns409NamingPrinter()
    {
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();
        Guid modelId = Guid.NewGuid();
        (Guid familyId, Guid variantId) =
            await SeedFamilyAsync(factory, "Farm Printer Family", modelId);
        await SeedPrinterAsync(factory, "Bench Printer", variantId);

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-printer-admin", "profile-family-printer@example.com");

        using HttpResponseMessage delete =
            await client.DeleteAsync($"/api/slicer/profiles/families/{familyId}");

        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using JsonDocument body = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("profile_family_in_use");
        body.RootElement.GetProperty("detail").GetString().Should().Contain("Bench Printer");
        worker.DeletedFamilyIds.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteFamily_WorkerDeleteUnavailable_Returns503_AndFamilyStaysListed()
    {
        var worker = new RecordingWorkerClient(new HttpRequestException("worker unavailable"));
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = await SeedFamilyAsync(factory, "Farm Resilient Family", modelId);

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-503-admin", "profile-family-503@example.com");

        using HttpResponseMessage delete =
            await client.DeleteAsync($"/api/slicer/profiles/families/{familyId}");

        delete.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using JsonDocument body = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString()
            .Should().Be("profile_family_worker_unavailable");

        using HttpResponseMessage getAfter =
            await client.GetAsync($"/api/slicer/profiles/families/{familyId}");
        getAfter.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the worker bundle delete is attempted before any DB mutation, so a worker failure " +
            "must leave the family fully listed and coherent");
        ProfileFamilySummaryDto? family =
            await getAfter.Content.ReadFromJsonAsync<ProfileFamilySummaryDto>();
        family!.FamilyName.Should().Be("Farm Resilient Family");
        family.Variants.Should().ContainSingle();
    }

    [Fact]
    public async Task EditFamily_UnknownId_Returns404FeatureErrorEnvelope()
    {
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-edit404-admin", "profile-family-edit404@example.com");

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/slicer/profiles/families/{Guid.NewGuid()}",
            new { name = "Renamed Family" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("profile_family_not_found");
        body.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.TryGetProperty("errors", out _).Should().BeFalse();
    }

    [Fact]
    public async Task EditFamily_RenameToExistingName_Returns409FeatureErrorEnvelope()
    {
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();
        (Guid familyId, _) = await SeedFamilyAsync(factory, "Original Family", Guid.NewGuid());
        _ = await SeedFamilyAsync(factory, "Taken Family", Guid.NewGuid());

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-rename409-admin", "profile-family-rename409@example.com");

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/slicer/profiles/families/{familyId}",
            new { name = "Taken Family" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("profile_family_name_conflict");
        body.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.TryGetProperty("errors", out _).Should().BeFalse();
        worker.DeletedFamilyIds.Should().BeEmpty();
    }

    [Fact]
    public async Task EditFamily_EmptyNozzleArray_Returns400FeatureErrorEnvelope()
    {
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();
        (Guid familyId, _) = await SeedFamilyAsync(factory, "Nozzle Guard Family", Guid.NewGuid());

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-emptynozzle-admin", "profile-family-emptynozzle@example.com");

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/slicer/profiles/families/{familyId}",
            new { nozzleDiameters = Array.Empty<double>() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("invalid_profile_family");
        body.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.TryGetProperty("errors", out _).Should().BeFalse();
    }

    [Fact]
    public async Task RenderFamily_UnknownId_Returns404FeatureErrorEnvelope()
    {
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-render404-admin", "profile-family-render404@example.com");

        using HttpResponseMessage response = await client.PostAsync(
            $"/api/slicer/profiles/families/{Guid.NewGuid()}/render", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("profile_family_not_found");
        body.RootElement.TryGetProperty("errors", out _).Should().BeFalse();
    }

    [Fact]
    public async Task RenderFamily_UnexpectedServiceFailure_Returns500FeatureErrorEnvelope()
    {
        // S3: an unexpected failure during render/install (here the worker catalog fetch throws a
        // NotSupportedException) must escape as the {code,detail} envelope, never a raw 500 / ASP.NET
        // problem shape. RecordingWorkerClient.GetCatalogAsync throws, so the re-render fails unexpectedly.
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();
        (Guid familyId, _) = await SeedFamilyAsync(
            factory, "Render Boom Family", Guid.NewGuid(), ProfileFamilyRenderStatus.Failed);

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-render500-admin", "profile-family-render500@example.com");

        using HttpResponseMessage response = await client.PostAsync(
            $"/api/slicer/profiles/families/{familyId}/render", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("profile_family_render_failed");
        body.RootElement.GetProperty("detail").GetString().Should().Be("Profile family re-render failed.");
        body.RootElement.TryGetProperty("errors", out _).Should().BeFalse();
    }

    [Fact]
    public async Task RenderStaleFamilies_NoStaleOrFailedFamilies_ReturnsEmptyArray()
    {
        // No worker version is available (GetActiveOrcaVersionAsync returns null), so detection-on-read
        // safely no-ops and a Healthy family is never swept into the batch: the result is an empty array.
        var worker = new RecordingWorkerClient();
        await using var factory = new LifecycleFactory(worker);
        await factory.ResetDatabaseAsync();
        _ = await SeedFamilyAsync(factory, "Healthy Batch Family", Guid.NewGuid());

        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-renderstale-admin", "profile-family-renderstale@example.com");

        using HttpResponseMessage response = await client.PostAsync(
            "/api/slicer/profiles/families/render-stale", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        RenderStaleFamiliesResponseDto? response2 =
            await response.Content.ReadFromJsonAsync<RenderStaleFamiliesResponseDto>();
        response2.Should().NotBeNull();
        response2!.Results.Should().BeEmpty();
        response2.RemainingCount.Should().Be(0);
    }

    private static async Task<(Guid FamilyId, Guid VariantId)> SeedFamilyAsync(
        CustomWebApplicationFactory factory,
        string name,
        Guid modelId,
        ProfileFamilyRenderStatus status = ProfileFamilyRenderStatus.Healthy)
    {
        Guid familyId = Guid.NewGuid();
        Guid variantId = Guid.NewGuid();
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        db.MachineModelProfiles.Add(new MachineModelProfile
        {
            Id = familyId,
            Name = name,
            Manufacturer = "Custom",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = modelId,
            Hash = familyId.ToString("N") + familyId.ToString("N"),
            IsSystem = false,
            RenderStatus = status,
            SourceMachineModelName = "Prusa Test",
            SlicerDistribution = "orca",
            RenderedForOrcaVersion = "2.4.2",
            LastRenderedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MachineProfiles =
            {
                new MachineProfile
                {
                    Id = variantId,
                    Name = $"{name} 0.4 nozzle",
                    Manufacturer = "Custom",
                    SlicerType = SlicerType.OrcaSlicer,
                    MachineModelProfileId = familyId,
                    Hash = variantId.ToString("N") + variantId.ToString("N"),
                    SourceSystemPresetName = "Prusa Test 0.4 nozzle",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            }
        });
        _ = await db.SaveChangesAsync();
        return (familyId, variantId);
    }

    private static async Task<Guid> SeedSliceJobAsync(
        CustomWebApplicationFactory factory,
        Guid machineProfileId,
        string status)
    {
        Guid jobId = Guid.NewGuid();
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        db.SliceJobs.Add(new SliceJob
        {
            Id = jobId,
            UserId = Guid.NewGuid(),
            MachineProfileId = machineProfileId,
            Status = status,
            CreatedAt = DateTime.UtcNow
        });
        _ = await db.SaveChangesAsync();
        return jobId;
    }

    private static async Task SeedPrinterAsync(
        CustomWebApplicationFactory factory,
        string name,
        Guid templateMachineProfileId)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        db.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = $"Catalog {name}"
        });
        db.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            ManufacturerId = manufacturerId,
            Name = $"Model {name}"
        });
        db.Printers.Add(new Printer
        {
            Id = Guid.NewGuid(),
            Name = name,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            ServerUrl = $"http://{Guid.NewGuid():N}.local",
            TemplateMachineProfileId = templateMachineProfileId
        });
        _ = await db.SaveChangesAsync();
    }

    private sealed class LifecycleFactory(IProfileFamilyWorkerClient workerClient)
        : CustomWebApplicationFactory
    {
        protected override void ConfigureTestServices(IServiceCollection services)
        {
            _ = services.RemoveAll<IProfileFamilyWorkerClient>();
            services.AddSingleton(workerClient);
        }
    }

    /// <summary>
    /// A stand-in worker client that records bundle deletions and can inject a delete failure.
    /// The list/get/delete lifecycle never fetches a catalog or writes a bundle, so those members
    /// throw to surface any accidental use.
    /// </summary>
    private sealed class RecordingWorkerClient(Exception? deleteFailure = null)
        : IProfileFamilyWorkerClient
    {
        public List<Guid> DeletedFamilyIds { get; } = [];

        public Task<(ProfileFamilyWorkerTarget Target, AllProfilesResponseDto Catalog)> GetCatalogAsync(
            string sourceManufacturer,
            string? orcaVersion,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task WriteBundleAsync(
            ProfileFamilyWorkerTarget target,
            ProfileFamilyBundleDto bundle,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteBundleAsync(string? orcaVersion, Guid familyId, CancellationToken ct)
        {
            DeletedFamilyIds.Add(familyId);
            return deleteFailure is null ? Task.CompletedTask : Task.FromException(deleteFailure);
        }

        // Detection-on-read calls this on the list/get path; returning null means "no worker online",
        // so staleness detection safely no-ops in the contract tests.
        public Task<string?> GetActiveOrcaVersionAsync(CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }
}
