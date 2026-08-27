extern alias OrcaWorker;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using OrcaWorkerCore = OrcaWorker::Farm.Slicer.Worker.Core;
using OrcaWorkerCtrl = OrcaWorker::Farm.OrcaSlicer.Worker.Controllers;
using OrcaWorkerSvc = OrcaWorker::Farm.OrcaSlicer.Worker.Services;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// End-to-end regression coverage for issue #2073: after cloning/creating a
/// printer family via <c>POST /api/slicer/profiles/clone-family</c>, an
/// immediate <c>GET /api/slicer/profiles/machine/for-model/{modelId}</c> MUST
/// return the newly-rendered machine profile without any worker restart,
/// reconciliation tick, or reload — with real HTTP transports between the
/// API and the worker in both directions.
/// </summary>
/// <remarks>
/// This test closes the gap that both <see cref="ProfileFamilyCloneLookupAcceptanceTests"/>
/// and <c>ProfileFamilyRealHttpRoundTripTests</c> leave open. The former
/// substitutes <c>InProcessProfileFamilyWorkerClient</c> and a fake
/// <c>WorkerLookupHandler</c>, bypassing the real HTTP path in both directions;
/// the latter drives the worker's real HTTP boundary but does not exercise
/// the API-side clone flow. This test exercises BOTH: the API's typed
/// <see cref="System.Net.Http.HttpClient"/> for
/// <c>ProfileFamilyWorkerClient</c> and the plain
/// <see cref="System.Net.Http.HttpClient"/> injected into
/// <c>ProfilesController</c> both target a hosted worker
/// <see cref="Microsoft.AspNetCore.TestHost.TestServer"/>, so any real-HTTP
/// gap — routing, model binding, auth filter, serialization, or the
/// <c>MutateAndReloadProfilesAsync</c> invocation embedded in the
/// controller — surfaces as a failing assertion.
/// </remarks>
public sealed class ProfileFamilyEndToEndHttpTests : IAsyncDisposable
{
    private const string SharedKey = "test-worker-key";
    private const string SourceManufacturer = "Prusa";
    private const string SourceModel = "Prusa Test";
    private const string SourceMachine = "Prusa Test 0.4 nozzle";
    private const string SourceFilament = "Generic PLA @Prusa Test";
    private const string SourceProcess = "0.20mm Standard @Prusa Test 0.4";
    private const string FamilyName = "E2E Farm";
    private const string WorkerBaseUrl = "http://e2e-worker";

    private readonly string _testRoot = Path.Join(
        AppContext.BaseDirectory,
        "profile-family-e2e",
        Guid.NewGuid().ToString("N"));

    private WebApplication? _worker;
    private E2EFactory? _factory;

    [Fact]
    public async Task CloneFamily_ThenForModelLookup_UsesRealHttpAcrossApiAndWorker()
    {
        string stockRoot = Path.Join(_testRoot, "stock");
        string overlayRoot = Path.Join(_testRoot, "overlay");
        string customRoot = Path.Join(_testRoot, "custom");
        string dbPath = Path.Join(_testRoot, "profile-cache.db");
        Directory.CreateDirectory(stockRoot);
        Directory.CreateDirectory(overlayRoot);
        Directory.CreateDirectory(customRoot);
        WriteStockProfiles(stockRoot);
        WriteStockProfiles(overlayRoot);

        HttpMessageHandler workerHandler = await StartWorkerAsync(
            stockRoot,
            overlayRoot,
            customRoot,
            dbPath);
        var recorder = new List<string>();
        var recordingHandler = new RecordingDelegatingHandler(workerHandler, recorder);

        _factory = new E2EFactory(recordingHandler);
        await _factory.ResetDatabaseAsync();

        Guid targetModelId = Guid.NewGuid();
        await SeedTargetModelAndWorkerAsync(_factory, targetModelId);

        using HttpClient client = await _factory.CreateAdminClientAsync(
            "profile-family-e2e-admin",
            "profile-family-e2e@example.com");

        using HttpResponseMessage before = await client.GetAsync(
            $"/api/slicer/profiles/machine/for-model/{targetModelId}");
        before.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "sanity check: before the clone, no OrcaSlicer alias exists for " +
            "this catalog model, so the lookup MUST report 'no_profiles_for_model'.");
        using (JsonDocument beforeBody = JsonDocument.Parse(
                   await before.Content.ReadAsStringAsync()))
        {
            beforeBody.RootElement.GetProperty("code").GetString()
                .Should().Be("no_profiles_for_model");
        }

        var request = new CloneProfileFamilyRequestDto
        {
            FamilyName = FamilyName,
            TargetPrinterModelId = targetModelId,
            SourceManufacturer = SourceManufacturer,
            SourceMachineModelName = SourceModel,
            NozzleDiameters = [0.4]
        };
        using HttpResponseMessage clone = await client.PostAsJsonAsync(
            "/api/slicer/profiles/clone-family",
            request);

        string cloneBody = await clone.Content.ReadAsStringAsync();
        string recordedRequests = string.Join("\n  ", recorder);
        clone.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the real HTTP round-trip PUT /api/profiles/custom-bundles/... " +
            "MUST succeed against the hosted worker — a non-201 here means " +
            "the API-side clone flow is failing over real HTTP (the seam " +
            "the existing acceptance test skips via InProcessProfileFamilyWorkerClient). " +
            $"Response body: {cloneBody}. Recorded HTTP requests through worker handler:\n  {recordedRequests}");

        using HttpResponseMessage after = await client.GetAsync(
            $"/api/slicer/profiles/machine/for-model/{targetModelId}");
        after.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "immediately after the clone, GET machine/for-model MUST return " +
            "200 — this is the exact user-visible symptom of issue #2073. If " +
            "this is 404 or 503 while the family is Healthy in the API DB, " +
            "the bug is a propagation gap between the install PUT and the " +
            "slice-time lookup GET over real HTTP.");
        List<MachineProfileDto>? profiles =
            await after.Content.ReadFromJsonAsync<List<MachineProfileDto>>();
        profiles.Should().NotBeNull();
        profiles!.Should().ContainSingle(profile =>
            profile.Name == $"{FamilyName} 0.4 nozzle" &&
            profile.PrinterModel == FamilyName);

        // Extend #2073 coverage beyond machine lookup: a real slice job also
        // needs a compatible process and filament for the cloned printer.
        // `POST process/for-machines` and `POST filament/for-machines` are
        // exactly the endpoints slice submission uses to enumerate what's
        // available, so if propagation broke for either axis (e.g., a future
        // regression in BuildProcessClones / BuildFilamentClones or the
        // machine_model parser fix that unblocks them), a slice couldn't
        // proceed even though machine lookup returned 200. Assert both.
        string clonedMachineName = $"{FamilyName} 0.4 nozzle";
        var machinesRequest = new { MachineNames = new[] { clonedMachineName } };

        using HttpResponseMessage processResponse = await client.PostAsJsonAsync(
            "/api/slicer/profiles/process/for-machines",
            machinesRequest);
        processResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "immediately after the clone, POST process/for-machines MUST return " +
            "200 for the cloned machine — a slice job selects a process next, " +
            "and if this fails, the user still can't slice against the new family " +
            "without a worker restart (the exact #2073 symptom, one axis over).");
        List<ProcessProfileDto>? processes =
            await processResponse.Content.ReadFromJsonAsync<List<ProcessProfileDto>>();
        processes.Should().NotBeNullOrEmpty(
            "the cloned family MUST expose at least one process profile compatible " +
            "with the cloned machine; empty means BuildProcessClones dropped the " +
            "source process or the reload didn't pick up the new custom bundle.");
        processes!.Should().Contain(
            profile => profile.CompatiblePrinters.Contains(clonedMachineName),
            "at least one returned process must be marked compatible with the cloned " +
            "machine so slice submission can select it without a manual reassignment.");

        using HttpResponseMessage filamentResponse = await client.PostAsJsonAsync(
            "/api/slicer/profiles/filament/for-machines",
            machinesRequest);
        filamentResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "immediately after the clone, POST filament/for-machines MUST return " +
            "200 for the cloned machine — a slice job selects a filament last, " +
            "and if this fails, the user still can't slice against the new family " +
            "without a worker restart (the exact #2073 symptom, one axis over).");
        List<FilamentProfileDto>? filaments =
            await filamentResponse.Content.ReadFromJsonAsync<List<FilamentProfileDto>>();
        filaments.Should().NotBeNullOrEmpty(
            "the cloned family MUST expose at least one filament profile compatible " +
            "with the cloned machine; empty means BuildFilamentClones dropped the " +
            "source filament (e.g., because Manufacturer resolved to " +
            "OrcaFilamentLibrary and was filtered) or the reload didn't pick up " +
            "the new custom bundle.");
        filaments!.Should().Contain(
            profile => profile.CompatiblePrinters.Contains(clonedMachineName),
            "at least one returned filament must be marked compatible with the cloned " +
            "machine so slice submission can select it without a manual reassignment.");
    }

    /// <summary>
    /// End-to-end proof for issue #2079: after cloning a family, DELETE
    /// <c>/api/slicer/profiles/families/{familyId}</c> MUST remove the rendered
    /// worker bundle and the OrcaSlicer model alias over real HTTP, so an
    /// immediate <c>GET machine/for-model/{modelId}</c> once again reports
    /// <c>no_profiles_for_model</c> — with no worker restart, reload, or
    /// reconciliation tick. This mirrors the #2073/#2077 assertion style in
    /// reverse: the clone makes the model resolve; the delete makes it stop.
    /// </summary>
    [Fact]
    public async Task DeleteFamily_RemovesWorkerBundleAndAlias_ForModelLookupNoLongerResolves()
    {
        string stockRoot = Path.Join(_testRoot, "stock");
        string overlayRoot = Path.Join(_testRoot, "overlay");
        string customRoot = Path.Join(_testRoot, "custom");
        string dbPath = Path.Join(_testRoot, "profile-cache.db");
        Directory.CreateDirectory(stockRoot);
        Directory.CreateDirectory(overlayRoot);
        Directory.CreateDirectory(customRoot);
        WriteStockProfiles(stockRoot);
        WriteStockProfiles(overlayRoot);

        HttpMessageHandler workerHandler = await StartWorkerAsync(
            stockRoot,
            overlayRoot,
            customRoot,
            dbPath);
        var recorder = new List<string>();
        var recordingHandler = new RecordingDelegatingHandler(workerHandler, recorder);

        _factory = new E2EFactory(recordingHandler);
        await _factory.ResetDatabaseAsync();

        Guid targetModelId = Guid.NewGuid();
        await SeedTargetModelAndWorkerAsync(_factory, targetModelId);

        using HttpClient client = await _factory.CreateAdminClientAsync(
            "profile-family-e2e-delete-admin",
            "profile-family-e2e-delete@example.com");

        var request = new CloneProfileFamilyRequestDto
        {
            FamilyName = FamilyName,
            TargetPrinterModelId = targetModelId,
            SourceManufacturer = SourceManufacturer,
            SourceMachineModelName = SourceModel,
            NozzleDiameters = [0.4]
        };
        using HttpResponseMessage clone = await client.PostAsJsonAsync(
            "/api/slicer/profiles/clone-family",
            request);
        string cloneBody = await clone.Content.ReadAsStringAsync();
        clone.StatusCode.Should().Be(
            HttpStatusCode.Created,
            $"the clone must succeed before delete can be exercised. Body: {cloneBody}");
        CloneProfileFamilyResponseDto? cloneResult =
            await clone.Content.ReadFromJsonAsync<CloneProfileFamilyResponseDto>();
        cloneResult.Should().NotBeNull();

        using HttpResponseMessage afterClone = await client.GetAsync(
            $"/api/slicer/profiles/machine/for-model/{targetModelId}");
        afterClone.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "sanity check: the clone must make the model resolve before the delete " +
            "is meaningful.");

        using HttpResponseMessage delete = await client.DeleteAsync(
            $"/api/slicer/profiles/families/{cloneResult!.FamilyId}");
        delete.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            "DELETE families/{id} must remove the worker bundle over real HTTP and " +
            "the OrcaSlicer alias, returning 204. Recorded worker requests:\n  " +
            string.Join("\n  ", recorder));

        using HttpResponseMessage afterDelete = await client.GetAsync(
            $"/api/slicer/profiles/machine/for-model/{targetModelId}");
        afterDelete.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "immediately after delete, GET machine/for-model MUST report " +
            "'no_profiles_for_model' again — proving the worker bundle was removed " +
            "and the alias invalidated over real HTTP without any worker restart.");
        using JsonDocument afterDeleteBody = JsonDocument.Parse(
            await afterDelete.Content.ReadAsStringAsync());
        afterDeleteBody.RootElement.GetProperty("code").GetString()
            .Should().Be("no_profiles_for_model");

        recorder.Should().Contain(
            entry => entry.Contains("DELETE", StringComparison.Ordinal) &&
                     entry.Contains("custom-bundles", StringComparison.Ordinal),
            "the API must have issued a real HTTP DELETE to the worker's " +
            "custom-bundles endpoint.");

        // S2: prove the bundle is actually GONE from the worker, not merely that the alias was dropped.
        // GET machine/for-model above short-circuits to 404 the moment the alias is removed, before ever
        // contacting the worker, so it cannot distinguish a real bundle delete from a no-op. POST
        // process/for-machines resolves by MACHINE NAME straight against the worker's live profile store
        // (no alias, no modelId), so if the cloned machine still resolved there the bundle would not have
        // been removed. It returned the cloned family's process before the delete (asserted in the clone
        // E2E test); after the delete the worker must no longer know that machine.
        string deletedMachineName = $"{FamilyName} 0.4 nozzle";
        using HttpResponseMessage workerAfterDelete = await client.PostAsJsonAsync(
            "/api/slicer/profiles/process/for-machines",
            new { MachineNames = new[] { deletedMachineName } });
        workerAfterDelete.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "process/for-machines answers straight from the worker and must stay reachable after delete.");
        List<ProcessProfileDto>? workerProcesses =
            await workerAfterDelete.Content.ReadFromJsonAsync<List<ProcessProfileDto>>();
        (workerProcesses ?? []).Should().NotContain(
            profile => profile.CompatiblePrinters.Contains(deletedMachineName),
            "after delete the worker itself must no longer resolve the family's machine profile — " +
            "proving the bundle was physically removed from the worker store, not just alias-hidden.");
    }

    /// <summary>
    /// Full profile-family lifecycle round trip (issue #2079, Phase 4 explicit acceptance
    /// criterion): create → list → edit → re-render → delete, all over real HTTP against the
    /// hosted worker, proving each stage takes effect immediately with NO worker restart,
    /// reconciliation tick, or reload. The edit is a rename, which re-renders the bundle (the
    /// bundle embeds the family name) and moves the OrcaSlicer alias; the surviving variant keeps
    /// its <c>MachineProfile.Id</c>; the explicit re-render recovers the same Healthy state; and
    /// the delete removes the worker bundle so the model stops resolving.
    /// </summary>
    [Fact]
    public async Task FullLifecycle_CreateListEditRerenderDelete_TakesEffectWithoutWorkerRestart()
    {
        string stockRoot = Path.Join(_testRoot, "stock");
        string overlayRoot = Path.Join(_testRoot, "overlay");
        string customRoot = Path.Join(_testRoot, "custom");
        string dbPath = Path.Join(_testRoot, "profile-cache.db");
        Directory.CreateDirectory(stockRoot);
        Directory.CreateDirectory(overlayRoot);
        Directory.CreateDirectory(customRoot);
        WriteStockProfiles(stockRoot);
        WriteStockProfiles(overlayRoot);

        HttpMessageHandler workerHandler = await StartWorkerAsync(
            stockRoot, overlayRoot, customRoot, dbPath);
        var recorder = new List<string>();
        var recordingHandler = new RecordingDelegatingHandler(workerHandler, recorder);

        _factory = new E2EFactory(recordingHandler);
        await _factory.ResetDatabaseAsync();

        Guid targetModelId = Guid.NewGuid();
        await SeedTargetModelAndWorkerAsync(_factory, targetModelId);

        using HttpClient client = await _factory.CreateAdminClientAsync(
            "profile-family-e2e-lifecycle-admin",
            "profile-family-e2e-lifecycle@example.com");

        // CREATE ----------------------------------------------------------------
        var createRequest = new CloneProfileFamilyRequestDto
        {
            FamilyName = FamilyName,
            TargetPrinterModelId = targetModelId,
            SourceManufacturer = SourceManufacturer,
            SourceMachineModelName = SourceModel,
            NozzleDiameters = [0.4]
        };
        using HttpResponseMessage clone = await client.PostAsJsonAsync(
            "/api/slicer/profiles/clone-family", createRequest);
        string cloneBody = await clone.Content.ReadAsStringAsync();
        clone.StatusCode.Should().Be(
            HttpStatusCode.Created,
            $"the create step must succeed before the lifecycle can continue. Body: {cloneBody}");
        CloneProfileFamilyResponseDto? created =
            await clone.Content.ReadFromJsonAsync<CloneProfileFamilyResponseDto>();
        created.Should().NotBeNull();
        Guid familyId = created!.FamilyId;
        Guid originalVariantId = created.MachineProfiles.Single().Id;

        using HttpResponseMessage afterCreate = await client.GetAsync(
            $"/api/slicer/profiles/machine/for-model/{targetModelId}");
        afterCreate.StatusCode.Should().Be(
            HttpStatusCode.OK, "immediately after create the model must resolve.");

        // LIST ------------------------------------------------------------------
        using HttpResponseMessage list = await client.GetAsync("/api/slicer/profiles/families");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        List<ProfileFamilySummaryDto>? families =
            await list.Content.ReadFromJsonAsync<List<ProfileFamilySummaryDto>>();
        families.Should().ContainSingle(family => family.FamilyId == familyId)
            .Which.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Healthy);

        // EDIT (rename → re-render → alias move) --------------------------------
        const string renamedFamily = "E2E Farm v2";
        using HttpResponseMessage edit = await client.PatchAsJsonAsync(
            $"/api/slicer/profiles/families/{familyId}",
            new { name = renamedFamily });
        string editBody = await edit.Content.ReadAsStringAsync();
        edit.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"a rename must re-render and move the alias over real HTTP. Body: {editBody}");
        ProfileFamilySummaryDto? edited =
            await edit.Content.ReadFromJsonAsync<ProfileFamilySummaryDto>();
        edited.Should().NotBeNull();
        edited!.FamilyName.Should().Be(renamedFamily);
        edited.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Healthy);
        edited.Variants.Single().MachineProfileId.Should().Be(
            originalVariantId,
            "an unrelated rename must preserve the surviving variant's MachineProfile.Id rather " +
            "than orphaning printer/job references.");

        using HttpResponseMessage afterEdit = await client.GetAsync(
            $"/api/slicer/profiles/machine/for-model/{targetModelId}");
        afterEdit.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "after the rename the alias must have moved so the model still resolves with no restart.");
        List<MachineProfileDto>? renamedProfiles =
            await afterEdit.Content.ReadFromJsonAsync<List<MachineProfileDto>>();
        renamedProfiles.Should().NotBeNull();
        renamedProfiles!.Should().ContainSingle(profile =>
            profile.Name == $"{renamedFamily} 0.4 nozzle" && profile.PrinterModel == renamedFamily);

        // RE-RENDER (explicit) --------------------------------------------------
        using HttpResponseMessage rerender = await client.PostAsync(
            $"/api/slicer/profiles/families/{familyId}/render", content: null);
        string rerenderBody = await rerender.Content.ReadAsStringAsync();
        rerender.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"an explicit re-render must recover the same Healthy state. Body: {rerenderBody}");
        ProfileFamilySummaryDto? rerendered =
            await rerender.Content.ReadFromJsonAsync<ProfileFamilySummaryDto>();
        rerendered!.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Healthy);
        rerendered.RenderedForOrcaVersion.Should().Be("2.4.2");
        rerendered.Variants.Single().MachineProfileId.Should().Be(
            originalVariantId, "an idempotent re-render must not churn the variant id.");

        using HttpResponseMessage afterRerender = await client.GetAsync(
            $"/api/slicer/profiles/machine/for-model/{targetModelId}");
        afterRerender.StatusCode.Should().Be(
            HttpStatusCode.OK, "the model must still resolve after the explicit re-render.");

        // H2: prove the worker actually HOLDS the family's bundle before the delete, so the post-delete
        // assertion below proves REMOVAL rather than absence. process/for-machines resolves by MACHINE NAME
        // straight against the worker's live profile store (no alias, no modelId), unlike machine/for-model
        // which short-circuits to 404 the instant the alias is dropped — before ever contacting the worker.
        // Use the POST-EDIT machine name (the rename above renamed the variant), not the original.
        string renamedMachineName = $"{renamedFamily} 0.4 nozzle";
        using HttpResponseMessage workerBeforeDelete = await client.PostAsJsonAsync(
            "/api/slicer/profiles/process/for-machines",
            new { MachineNames = new[] { renamedMachineName } });
        workerBeforeDelete.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "process/for-machines answers straight from the worker and must be reachable before delete.");
        List<ProcessProfileDto>? processesBeforeDelete =
            await workerBeforeDelete.Content.ReadFromJsonAsync<List<ProcessProfileDto>>();
        processesBeforeDelete.Should().Contain(
            profile => profile.CompatiblePrinters.Contains(renamedMachineName),
            "before delete the worker itself must resolve the family's (renamed) machine profile — " +
            "establishing the bundle is physically present on the worker so the post-delete check " +
            "proves removal, not mere absence.");

        // DELETE ----------------------------------------------------------------
        using HttpResponseMessage delete = await client.DeleteAsync(
            $"/api/slicer/profiles/families/{familyId}");
        delete.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            "delete must remove the worker bundle and alias. Recorded worker requests:\n  " +
            string.Join("\n  ", recorder));

        using HttpResponseMessage afterDelete = await client.GetAsync(
            $"/api/slicer/profiles/machine/for-model/{targetModelId}");
        afterDelete.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "after delete the model must stop resolving with no worker restart.");
        using JsonDocument afterDeleteBody = JsonDocument.Parse(
            await afterDelete.Content.ReadAsStringAsync());
        afterDeleteBody.RootElement.GetProperty("code").GetString()
            .Should().Be("no_profiles_for_model");

        // H2: prove the bundle is actually GONE from the worker, not merely alias-hidden. machine/for-model
        // above short-circuits to 404 the moment the alias is removed, before contacting the worker, so a
        // completely no-op DeleteBundleAsync would still make it pass. process/for-machines resolves by
        // MACHINE NAME straight against the worker store, so if the renamed machine still resolved there the
        // bundle was NOT removed. It resolved before the delete (asserted above); it must not now — this is
        // the assertion that fails if DeleteBundleAsync were a no-op.
        using HttpResponseMessage workerAfterDelete = await client.PostAsJsonAsync(
            "/api/slicer/profiles/process/for-machines",
            new { MachineNames = new[] { renamedMachineName } });
        workerAfterDelete.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "process/for-machines answers straight from the worker and must stay reachable after delete.");
        List<ProcessProfileDto>? processesAfterDelete =
            await workerAfterDelete.Content.ReadFromJsonAsync<List<ProcessProfileDto>>();
        (processesAfterDelete ?? []).Should().NotContain(
            profile => profile.CompatiblePrinters.Contains(renamedMachineName),
            "after delete the worker itself must no longer resolve the family's (renamed) machine profile — " +
            "proving the bundle was physically removed from the worker store, not just alias-hidden. A no-op " +
            "DeleteBundleAsync would leave the machine resolving here and fail this assertion.");

        using HttpResponseMessage getGone = await client.GetAsync(
            $"/api/slicer/profiles/families/{familyId}");
        getGone.StatusCode.Should().Be(
            HttpStatusCode.NotFound, "the family row must be gone from the API DB too.");

        using HttpResponseMessage listGone = await client.GetAsync("/api/slicer/profiles/families");
        List<ProfileFamilySummaryDto>? remaining =
            await listGone.Content.ReadFromJsonAsync<List<ProfileFamilySummaryDto>>();
        remaining.Should().BeEmpty("no families remain after the round trip.");
    }

    /// <summary>
    /// Issue #2079 §4/§5 acceptance criterion: a re-render that fails must never leave the farm
    /// worse off than before it ran. Re-binding a Healthy family to a source machine model that
    /// does not resolve in the live catalog fails with <c>422 source_preset_unavailable</c> BEFORE
    /// any worker or DB mutation, so the previously installed good bundle is preserved and the
    /// model still slices via <c>GET machine/for-model/{modelId}</c> — with no worker restart.
    /// </summary>
    [Fact]
    public async Task EditFamily_SourceRebindToUnavailableModel_PreservesPreviousGoodBundle()
    {
        string stockRoot = Path.Join(_testRoot, "stock");
        string overlayRoot = Path.Join(_testRoot, "overlay");
        string customRoot = Path.Join(_testRoot, "custom");
        string dbPath = Path.Join(_testRoot, "profile-cache.db");
        Directory.CreateDirectory(stockRoot);
        Directory.CreateDirectory(overlayRoot);
        Directory.CreateDirectory(customRoot);
        WriteStockProfiles(stockRoot);
        WriteStockProfiles(overlayRoot);

        HttpMessageHandler workerHandler = await StartWorkerAsync(
            stockRoot, overlayRoot, customRoot, dbPath);
        var recorder = new List<string>();
        var recordingHandler = new RecordingDelegatingHandler(workerHandler, recorder);

        _factory = new E2EFactory(recordingHandler);
        await _factory.ResetDatabaseAsync();

        Guid targetModelId = Guid.NewGuid();
        await SeedTargetModelAndWorkerAsync(_factory, targetModelId);

        using HttpClient client = await _factory.CreateAdminClientAsync(
            "profile-family-e2e-preserve-admin",
            "profile-family-e2e-preserve@example.com");

        var createRequest = new CloneProfileFamilyRequestDto
        {
            FamilyName = FamilyName,
            TargetPrinterModelId = targetModelId,
            SourceManufacturer = SourceManufacturer,
            SourceMachineModelName = SourceModel,
            NozzleDiameters = [0.4]
        };
        using HttpResponseMessage clone = await client.PostAsJsonAsync(
            "/api/slicer/profiles/clone-family", createRequest);
        string cloneBody = await clone.Content.ReadAsStringAsync();
        clone.StatusCode.Should().Be(
            HttpStatusCode.Created,
            $"the clone must succeed so there is a good bundle to preserve. Body: {cloneBody}");
        CloneProfileFamilyResponseDto? created =
            await clone.Content.ReadFromJsonAsync<CloneProfileFamilyResponseDto>();
        Guid familyId = created!.FamilyId;

        using HttpResponseMessage before = await client.GetAsync(
            $"/api/slicer/profiles/machine/for-model/{targetModelId}");
        before.StatusCode.Should().Be(
            HttpStatusCode.OK, "sanity: the good bundle resolves before the failed re-render.");

        // Force a re-render failure: re-bind to a source machine model that does not exist in the
        // live worker catalog. The source manufacturer cannot be derived, so the edit fails 422
        // before any worker PUT or DB mutation.
        using HttpResponseMessage edit = await client.PatchAsJsonAsync(
            $"/api/slicer/profiles/families/{familyId}",
            new { sourceMachineModelName = "Ghost Printer That Does Not Exist" });
        edit.StatusCode.Should().Be(
            HttpStatusCode.UnprocessableEntity,
            "re-binding to a source that no longer resolves must fail 422 source_preset_unavailable.");
        using JsonDocument editBody = JsonDocument.Parse(await edit.Content.ReadAsStringAsync());
        editBody.RootElement.GetProperty("code").GetString().Should().Be("source_preset_unavailable");
        editBody.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        editBody.RootElement.TryGetProperty("errors", out _).Should().BeFalse();

        // The previous good bundle must be preserved: the model still slices with no worker restart.
        using HttpResponseMessage after = await client.GetAsync(
            $"/api/slicer/profiles/machine/for-model/{targetModelId}");
        after.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a failed re-render must leave the previously installed good bundle intact so the " +
            "model still resolves — the farm must never be left worse off than before the edit.");
        List<MachineProfileDto>? profiles =
            await after.Content.ReadFromJsonAsync<List<MachineProfileDto>>();
        profiles.Should().NotBeNull();
        profiles!.Should().ContainSingle(profile => profile.Name == $"{FamilyName} 0.4 nozzle");

        // The family row is untouched (still Healthy, unchanged source) because the failure fired
        // before any mutation.
        using HttpResponseMessage getFamily = await client.GetAsync(
            $"/api/slicer/profiles/families/{familyId}");
        getFamily.StatusCode.Should().Be(HttpStatusCode.OK);
        ProfileFamilySummaryDto? family =
            await getFamily.Content.ReadFromJsonAsync<ProfileFamilySummaryDto>();
        family!.RenderStatus.Should().Be(
            ProfileFamilyRenderStatus.Healthy,
            "the pre-mutation 422 must not flip the family to Failed.");
        family.SourceMachineModelName.Should().Be(
            SourceModel, "the unchanged source binding must survive the rejected re-bind.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.StopAsync();
            await _worker.DisposeAsync();
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        string cacheDatabasePath = Path.Join(_testRoot, "profile-cache.db");
        if (File.Exists(cacheDatabasePath))
        {
            using var pooledConnection = new SqliteConnection(
                $"Data Source={cacheDatabasePath};Mode=ReadWriteCreate;Cache=Shared");
            SqliteConnection.ClearPool(pooledConnection);
        }

        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, recursive: true);
            }
            catch (IOException)
            {
                // Symlinks or SQLite pool cleanup races may briefly hold the
                // directory. Best-effort cleanup — CI's per-run temp directory
                // is disposed by the test harness anyway.
            }
        }
    }

    /// <summary>
    /// Stands up a real worker <see cref="WebApplication"/> hosted on a
    /// <see cref="TestServer"/>. Its <c>CustomProfilesController</c>,
    /// <c>SlicerProfilesController</c>, <c>CustomProfileBundleStore</c>,
    /// <c>CachedOrcaProfilesService</c>, and shared-key auth filter are all
    /// registered exactly as in the real worker's Program.cs, so a real HTTP
    /// PUT from the API round-trips through every real seam.
    /// </summary>
    private async Task<HttpMessageHandler> StartWorkerAsync(
        string stockRoot,
        string overlayRoot,
        string customRoot,
        string dbPath)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["WorkerAuth:SharedKey"] = SharedKey
            });

        _ = builder.Services.AddControllers()
            .AddApplicationPart(typeof(OrcaWorkerCtrl.CustomProfilesController).Assembly);

        _ = builder.Services.AddSingleton(sp =>
            new OrcaWorkerSvc.CachedOrcaProfilesService(
                NullLogger<OrcaWorkerSvc.CachedOrcaProfilesService>.Instance,
                profilesPath: overlayRoot,
                dbPath: dbPath,
                customProfilesPath: customRoot));
        _ = builder.Services.AddSingleton(sp =>
            new OrcaWorkerSvc.CustomProfileBundleStore(
                NullLogger<OrcaWorkerSvc.CustomProfileBundleStore>.Instance,
                stockProfilesPath: stockRoot,
                overlayProfilesPath: overlayRoot,
                customProfilesPath: customRoot));
        _ = builder.Services.AddSingleton<OrcaWorkerSvc.CustomProfilesReconciliationState>();
        _ = builder.Services.AddSingleton<OrcaWorkerCtrl.WorkerSharedKeyValidator>();
        _ = builder.Services.AddSingleton<OrcaWorkerCore.ISlicerProfilesService>(sp =>
            sp.GetRequiredService<OrcaWorkerSvc.CachedOrcaProfilesService>());

        WebApplication app = builder.Build();
        _worker = app;
        _ = app.UseRouting();
        _ = app.MapControllers();
        await app.StartAsync();
        TestServer testServer = app.GetTestServer();
        testServer.BaseAddress = new Uri(WorkerBaseUrl);
        return testServer.CreateHandler();
    }

    private static async Task SeedTargetModelAndWorkerAsync(
        CustomWebApplicationFactory factory,
        Guid targetModelId)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext appDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid manufacturerId = Guid.NewGuid();
        appDb.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = "E2E Catalog"
        });
        appDb.PrinterModels.Add(new PrinterModel
        {
            Id = targetModelId,
            ManufacturerId = manufacturerId,
            Name = "E2E Target"
        });
        await appDb.SaveChangesAsync();

        SlicerDbContext slicerDb = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        slicerDb.SlicerServices.Add(new SlicerService
        {
            Id = Guid.NewGuid(),
            Name = "e2e-worker",
            SlicerType = (int)SlicerType.OrcaSlicer,
            Version = "2.4.2",
            Host = WorkerBaseUrl,
            Status = "Online",
            LastSeen = DateTime.UtcNow,
            CapabilitiesJson =
                $"[\"{CalibrationContractConstants.UpstreamSlicerCapability}\"]"
        });
        await slicerDb.SaveChangesAsync();
    }

    /// <summary>
    /// Writes a minimal stock manufacturer bundle that exercises the worker's
    /// real HTTP <c>GetAllProfilesAsync</c> path. That path enumerates
    /// <c>machine_model_list</c> entries (not <c>machine_list</c>), so the
    /// bundle here declares one machine model and one machine, with the
    /// machine model file living at the referenced sub-path so
    /// <c>OrcaProfilesService.ListAvailableMachineModelProfilesAsync</c> picks
    /// it up. The bundle also includes one filament and one process profile
    /// tied to the source machine — needed so <c>BuildFilamentClones</c> /
    /// <c>BuildProcessClones</c> (which are downstream of the fix) actually
    /// have material to clone. Without those, the immediate-slicing contract
    /// (issue #2073) is only proven for the machine axis; a slice job also
    /// needs a compatible filament and process to run, so this test now
    /// asserts all three axes propagate through the real HTTP round-trip. The
    /// caller writes to both the stock root and the overlay root so
    /// <c>CachedOrcaProfilesService</c> and <c>CustomProfileBundleStore</c>
    /// see the same content without needing filesystem symlinks (which require
    /// elevated privileges on Windows).
    /// </summary>
    private static void WriteStockProfiles(string profilesPath)
    {
        string machinePath = Path.Join(profilesPath, SourceManufacturer, "machine");
        string machineModelPath = Path.Join(
            profilesPath, SourceManufacturer, "machine_model");
        string filamentPath = Path.Join(profilesPath, SourceManufacturer, "filament");
        string processPath = Path.Join(profilesPath, SourceManufacturer, "process");
        Directory.CreateDirectory(machinePath);
        Directory.CreateDirectory(machineModelPath);
        Directory.CreateDirectory(filamentPath);
        Directory.CreateDirectory(processPath);
        File.WriteAllText(
            Path.Join(profilesPath, $"{SourceManufacturer}.json"),
            $$"""
            {
              "name": "{{SourceManufacturer}}",
              "version": "01.00.00.00",
              "machine_model_list": [
                {
                  "name": "{{SourceModel}}",
                  "sub_path": "machine_model/{{SourceModel}}.json"
                }
              ],
              "machine_list": [
                {
                  "name": "{{SourceMachine}}",
                  "sub_path": "machine/{{SourceMachine}}.json"
                }
              ],
              "process_list": [
                {
                  "name": "{{SourceProcess}}",
                  "sub_path": "process/{{SourceProcess}}.json"
                }
              ],
              "filament_list": [
                {
                  "name": "{{SourceFilament}}",
                  "sub_path": "filament/{{SourceFilament}}.json"
                }
              ]
            }
            """,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Join(machineModelPath, $"{SourceModel}.json"),
            $$"""
            {
              "type": "machine_model",
              "name": "{{SourceModel}}",
              "model_id": "{{SourceModel}}",
              "nozzle_diameter": "0.4",
              "family": "{{SourceManufacturer}}",
              "instantiation": "true"
            }
            """,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Join(machinePath, $"{SourceMachine}.json"),
            $$"""
            {
              "type": "machine",
              "name": "{{SourceMachine}}",
              "from": "system",
              "instantiation": "true",
              "printer_model": "{{SourceModel}}",
              "nozzle_diameter": ["0.4"],
              "max_layer_height": ["0.32"]
            }
            """,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Join(filamentPath, $"{SourceFilament}.json"),
            $$"""
            {
              "type": "filament",
              "name": "{{SourceFilament}}",
              "from": "system",
              "instantiation": "true",
              "filament_vendor": ["E2E Vendor"],
              "filament_type": ["PLA"],
              "compatible_printers": ["{{SourceMachine}}"]
            }
            """,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Join(processPath, $"{SourceProcess}.json"),
            $$"""
            {
              "type": "process",
              "name": "{{SourceProcess}}",
              "from": "system",
              "instantiation": "true",
              "layer_height": "0.2",
              "compatible_printers": ["{{SourceMachine}}"]
            }
            """,
            Encoding.UTF8);
    }

    /// <summary>
    /// Wraps a <see cref="HttpMessageHandler"/> so the test can record every
    /// request URI + method that the API side sends toward the worker. If a
    /// clone attempt fails, the recorded list surfaces which HTTP call in the
    /// chain (catalog fetch, bundle PUT, or slice-time GET) failed — a
    /// diagnostic the raw 503 body doesn't give you.
    /// </summary>
    private sealed class RecordingDelegatingHandler(
        HttpMessageHandler innerHandler,
        List<string> recorder) : DelegatingHandler(innerHandler)
    {
        // Defense-in-depth: the outer test calls the API sequentially, but
        // nothing prevents a future refactor from firing concurrent worker
        // requests behind a single API call — a concurrent Add on a
        // List<string> can throw IndexOutOfRangeException or produce a
        // corrupted diagnostic string. Serializing all recorder access under
        // this lock keeps append + read-for-diagnostic both atomic.
        private readonly Lock _recorderLock = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string uri = request.RequestUri?.ToString() ?? "<null>";
            try
            {
                HttpResponseMessage response = await base
                    .SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                lock (_recorderLock)
                {
                    recorder.Add($"{request.Method} {uri} -> {(int)response.StatusCode}");
                }

                return response;
            }
            catch (Exception ex)
            {
                lock (_recorderLock)
                {
                    recorder.Add($"{request.Method} {uri} -> EXCEPTION {ex.GetType().Name}: {ex.Message}");
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Extends the shared <see cref="CustomWebApplicationFactory"/> to redirect
    /// both the typed <see cref="System.Net.Http.HttpClient"/> registered for
    /// <c>IProfileFamilyWorkerClient</c> and the general
    /// <see cref="System.Net.Http.HttpClient"/> injected into
    /// <c>ProfilesController</c> to a shared handler that targets the hosted
    /// worker <see cref="TestServer"/>. This is the surgical difference from
    /// <see cref="ProfileFamilyCloneLookupAcceptanceTests"/>'s
    /// <c>AcceptanceFactory</c>, which shortcuts around the client entirely
    /// via <c>InProcessProfileFamilyWorkerClient</c>.
    /// </summary>
    private sealed class E2EFactory(HttpMessageHandler workerHandler) : CustomWebApplicationFactory
    {
        protected override void ConfigureTestServices(IServiceCollection services)
        {
            // Directly replace IProfileFamilyWorkerClient with a fresh instance
            // that uses our recording handler. This is more robust than
            // ConfigurePrimaryHttpMessageHandler on the named client, because
            // it doesn't depend on which name AddHttpClient<TClient,TImpl> uses
            // internally.
            _ = services.RemoveAll<IProfileFamilyWorkerClient>();
            _ = services.AddScoped<IProfileFamilyWorkerClient>(sp =>
                new ProfileFamilyWorkerClient(
                    new HttpClient(workerHandler, disposeHandler: false)
                    {
                        Timeout = TimeSpan.FromMinutes(2)
                    },
                    sp.GetRequiredService<ISlicersService>(),
                    sp.GetRequiredService<IConfiguration>(),
                    sp.GetRequiredService<ILogger<ProfileFamilyWorkerClient>>()));

            services.RemoveAll<HttpClient>();
            services.AddSingleton(new HttpClient(workerHandler, disposeHandler: false)
            {
                BaseAddress = new Uri(WorkerBaseUrl)
            });
        }
    }
}
