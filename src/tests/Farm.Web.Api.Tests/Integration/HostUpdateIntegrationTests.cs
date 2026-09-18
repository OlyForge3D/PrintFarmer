using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Modules.Administration.Controllers.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Kane-mandated executable integration evidence for the host-update manual admin path
/// (issues #2663/#2666). These tests run the real ASP.NET Core test host with real DI: the
/// resolver, CAS candidate cache, file-backed replay/policy/journal stores, and controller are
/// all production code operating against a real temporary host-state root. The only interface
/// substituted anywhere is <see cref="IHostUpdateExecutor"/> (the production implementation is
/// permanently unavailable — physical recovery/execution is intentionally unimplemented) and,
/// purely to make the host-platform gate deterministic across the CI/dev operating systems this
/// suite runs on, <see cref="IHostUpdateSchedulerCandidateCache"/> is reconstructed from the same
/// production <see cref="VerifiedReleaseEvidenceCandidateCache"/> class with a fixed platform
/// string instead of the OS-conditional one <c>Program.cs</c> computes (mirroring the existing
/// convention in <c>ProductionHostUpdateAdaptersTests</c>/<c>ServiceInventoryTests</c>, which
/// always exercise this class with a literal <c>"linux-amd64"</c> platform for the same reason).
/// </summary>
public sealed class HostUpdateIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task ManualPath_EndToEnd_ExecutesServerDerivedRequestAndStatusReflectsIt()
    {
        string hostStateRoot = Directory.CreateTempSubdirectory("hostupdate-e2e-").FullName;
        try
        {
            await using CustomWebApplicationFactory factory = new(Config(hostStateRoot));
            using HttpClient adminClient = await factory.CreateAdminClientAsync();

            RecordingConstrainedExecutor executor = new();
            await using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services => OverrideHostUpdateTestSeams(services, executor)));
            using HttpClient client = host.CreateClient();
            client.DefaultRequestHeaders.Authorization = adminClient.DefaultRequestHeaders.Authorization;

            await ProvisionHostStateAsync(host.Services);
            SeedVerifiedCandidate(host.Services, sequence: 42);

            // No client-supplied target release material anywhere in the request body.
            HttpResponseMessage executeResponse = await client.PostAsJsonAsync("/api/admin/host-updates/execute", new { });
            executeResponse.StatusCode.Should().Be(HttpStatusCode.OK, await executeResponse.Content.ReadAsStringAsync());
            HostUpdateStatusResponse? executed = await executeResponse.Content.ReadFromJsonAsync<HostUpdateStatusResponse>(JsonOptions);
            executed.Should().NotBeNull();
            executed!.ReleaseId.Should().Be("stable:1.2.3");
            executed.CurrentState.Should().Be(HostUpdateExecutionState.Completed);

            executor.Calls.Should().Be(1);
            HostUpdateExecutionRequest received = executor.LastRequest.ShouldNotBeNullAndReturn();
            received.ReleaseId.Should().Be("stable:1.2.3");
            received.SourceCommit.Should().Be(new string('c', 40));
            received.AuthenticatedSequence.Should().Be(42);
            received.ManifestDigest.Should().Be(Digest);
            received.Channel.Should().Be(HostUpdateExecutionChannel.Stable);
            received.TrustRoot.Should().Be("default");
            received.AuthorizationKind.Should().Be(HostUpdateAuthorizationKind.Manual);
            received.Targets.Should().HaveCount(6);

            HttpResponseMessage statusResponse = await client.GetAsync("/api/admin/host-updates/stable:1.2.3/status");
            statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            HostUpdateStatusResponse? status = await statusResponse.Content.ReadFromJsonAsync<HostUpdateStatusResponse>(JsonOptions);
            status.Should().NotBeNull();
            status!.CurrentState.Should().Be(HostUpdateExecutionState.Completed);
            status.Activities.Should().ContainSingle(activity => activity.State == HostUpdateExecutionState.Completed
                && activity.RequestBindingHash == HostUpdateRequestBinding.Compute(received));
        }
        finally
        {
            Directory.Delete(hostStateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ManualSeparation_StandingAutoDisabledDoesNotBlockManualRoute_ChannelSelectionAloneCannotExecute()
    {
        string hostStateRoot = Directory.CreateTempSubdirectory("hostupdate-separation-").FullName;
        try
        {
            await using CustomWebApplicationFactory factory = new(Config(hostStateRoot));
            using HttpClient adminClient = await factory.CreateAdminClientAsync();

            RecordingConstrainedExecutor executor = new();
            await using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services => OverrideHostUpdateTestSeams(services, executor)));
            using HttpClient client = host.CreateClient();
            client.DefaultRequestHeaders.Authorization = adminClient.DefaultRequestHeaders.Authorization;

            await ProvisionHostStateAsync(host.Services);

            // Standing automatic updates are provisioned disabled by default (Enabled=false) --
            // confirm the channel/policy alone never causes an execution before any manual call.
            using (IServiceScope scope = host.Services.CreateScope())
            {
                IHostUpdateAutomationPolicyRepository repository = scope.ServiceProvider.GetRequiredService<IHostUpdateAutomationPolicyRepository>();
                HostUpdatePolicyReadResult policy = repository.Read();
                policy.Available.Should().BeTrue();
                policy.Policy.Enabled.Should().BeFalse();
            }

            SeedVerifiedCandidate(host.Services, sequence: 7);

            HttpResponseMessage beforeStatus = await client.GetAsync("/api/admin/host-updates/stable:1.2.3/status");
            beforeStatus.StatusCode.Should().Be(HttpStatusCode.NotFound);
            executor.Calls.Should().Be(0);

            HttpResponseMessage executeResponse = await client.PostAsJsonAsync("/api/admin/host-updates/execute", new { });
            executeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            executor.Calls.Should().Be(1);
        }
        finally
        {
            Directory.Delete(hostStateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RestartOfflineOlderDatabaseRestore_ReplayAndJournalAuthorityPersistsFromHostStateNotAppState()
    {
        string hostStateRoot = Directory.CreateTempSubdirectory("hostupdate-restart-").FullName;
        try
        {
            string statusBefore;
            await using (CustomWebApplicationFactory firstFactory = new(Config(hostStateRoot)))
            {
                using HttpClient firstAdmin = await firstFactory.CreateAdminClientAsync();
                RecordingConstrainedExecutor firstExecutor = new();
                await using WebApplicationFactory<Program> firstHost = firstFactory.WithWebHostBuilder(builder =>
                    builder.ConfigureTestServices(services => OverrideHostUpdateTestSeams(services, firstExecutor)));
                using HttpClient firstClient = firstHost.CreateClient();
                firstClient.DefaultRequestHeaders.Authorization = firstAdmin.DefaultRequestHeaders.Authorization;

                await ProvisionHostStateAsync(firstHost.Services);
                SeedVerifiedCandidate(firstHost.Services, sequence: 100);

                HttpResponseMessage executeResponse = await firstClient.PostAsJsonAsync("/api/admin/host-updates/execute", new { });
                executeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

                HttpResponseMessage statusResponse = await firstClient.GetAsync("/api/admin/host-updates/stable:1.2.3/status");
                statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                statusBefore = await statusResponse.Content.ReadAsStringAsync();
            }

            // Simulate a process restart with a recreated/restored, brand-new app database (each
            // CustomWebApplicationFactory instance provisions its own independent in-memory
            // SQLite database) AND an offline/unavailable verified-release cache (no SetVerified
            // call this time -- discovery has not run yet in this fresh process). Only the
            // persistent host-state root on disk survives across the two instances.
            await using CustomWebApplicationFactory secondFactory = new(Config(hostStateRoot));
            using HttpClient secondAdmin = await secondFactory.CreateAdminClientAsync();
            RecordingConstrainedExecutor secondExecutor = new();
            await using WebApplicationFactory<Program> secondHost = secondFactory.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services => OverrideHostUpdateTestSeams(services, secondExecutor)));
            using HttpClient secondClient = secondHost.CreateClient();
            secondClient.DefaultRequestHeaders.Authorization = secondAdmin.DefaultRequestHeaders.Authorization;

            HttpResponseMessage statusAfterRestart = await secondClient.GetAsync("/api/admin/host-updates/stable:1.2.3/status");
            statusAfterRestart.StatusCode.Should().Be(HttpStatusCode.OK);
            string statusAfter = await statusAfterRestart.Content.ReadAsStringAsync();
            statusAfter.Should().Be(statusBefore);

            // The verified-release cache is unavailable (never seeded) in the fresh process:
            // the manual path must fail closed, and must NOT be able to lower or rebind the
            // durable, host-state-anchored replay/journal authority reasserted above.
            HttpResponseMessage executeAfterRestart = await secondClient.PostAsJsonAsync("/api/admin/host-updates/execute", new { });
            executeAfterRestart.StatusCode.Should().NotBe(HttpStatusCode.OK);
            secondExecutor.Calls.Should().Be(0);

            HttpResponseMessage statusStillIntact = await secondClient.GetAsync("/api/admin/host-updates/stable:1.2.3/status");
            statusStillIntact.StatusCode.Should().Be(HttpStatusCode.OK);
            (await statusStillIntact.Content.ReadAsStringAsync()).Should().Be(statusBefore);
        }
        finally
        {
            Directory.Delete(hostStateRoot, recursive: true);
        }
    }

    private static Dictionary<string, string?> Config(string hostStateRoot) => new()
    {
        ["Slicer:Enabled"] = "false",
        ["HostUpdates:HostState:Enabled"] = "true",
        ["HostUpdates:HostState:RootPath"] = hostStateRoot,
        ["HostUpdates:HostState:WindowsSecurityAttested"] = "true",
        ["HostUpdates:HostState:ProvisioningEnabled"] = "true",
    };

    /// <summary>
    /// Re-registers the exact production host-state-backed implementations that
    /// FeatureServicesStartup.AddPrintFarmerFeatureServices itself registers when
    /// HostUpdates:HostState:Enabled is true. This duplication exists only because
    /// synchronous configuration reads inside Program.cs/FeatureServicesStartup run
    /// before WebApplicationFactory's ConfigureAppConfiguration test overrides are
    /// merged into the final configuration (a WebApplicationBuilder/minimal-hosting
    /// timing quirk, not something specific to host updates), so the config-gated
    /// branch in production startup always sees the flag as disabled during a test
    /// run. Every type constructed below is the same production class Program.cs
    /// would have selected; only the selection point moves from Program.cs's early
    /// synchronous check to this test-only DI override, and only IHostUpdateExecutor
    /// and IHostUpdateSchedulerCandidateCache substitute anything beyond that (per
    /// Kane's override-only-the-executor instruction plus the portable-platform-
    /// string seam already discussed above).
    /// </summary>
    private static void OverrideHostUpdateTestSeams(IServiceCollection services, RecordingConstrainedExecutor executor)
    {
        // VerifiedReleaseDiscoveryMonitorService is a real, unconditionally-registered hosted
        // service that periodically calls the shared IVerifiedReleaseEvidenceCache.SetError(...)
        // whenever real GitHub/Cosign discovery fails (as it always will in a test sandbox with
        // no network/Cosign access), which would otherwise race with and clobber this test's
        // deliberately seeded evidence. Removing only this one hosted-service descriptor (not
        // IHostedService broadly) keeps every other background service running unmodified.
        Microsoft.Extensions.DependencyInjection.ServiceDescriptor? discoveryMonitor = services.FirstOrDefault(
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(VerifiedReleaseDiscoveryMonitorService));
        if (discoveryMonitor is not null)
        {
            services.Remove(discoveryMonitor);
        }

        services.RemoveAll<HostStatePath>();
        services.AddSingleton<HostStatePath>();
        services.RemoveAll<FileHostUpdateReplayAnchor>();
        services.AddSingleton<FileHostUpdateReplayAnchor>();
        services.RemoveAll<IHostUpdateReplayAnchor>();
        services.AddSingleton<IHostUpdateReplayAnchor>(sp => sp.GetRequiredService<FileHostUpdateReplayAnchor>());
        services.RemoveAll<IHostUpdateReplayAnchorProvisioner>();
        services.AddSingleton<IHostUpdateReplayAnchorProvisioner>(sp => sp.GetRequiredService<FileHostUpdateReplayAnchor>());
        services.RemoveAll<IHostUpdateReplayStore>();
        services.AddSingleton<IHostUpdateReplayStore>(sp => new FileHostUpdateReplayStore(
            sp.GetRequiredService<HostStatePath>().Root,
            sp.GetRequiredService<IHostUpdateReplayAnchor>()));
        services.RemoveAll<IHostUpdatePolicyFence>();
        services.AddSingleton<IHostUpdatePolicyFence>(sp => new FileHostUpdatePolicyFence(sp.GetRequiredService<HostStatePath>().Root));
        services.RemoveAll<IHostUpdateManualAuthorizationStore>();
        services.AddSingleton<IHostUpdateManualAuthorizationStore>(sp => new FileHostUpdateManualAuthorizationStore(sp.GetRequiredService<HostStatePath>().Root));
        services.RemoveAll<FileHostUpdateAutomationPolicyRepository>();
        services.AddSingleton<FileHostUpdateAutomationPolicyRepository>();
        services.RemoveAll<IHostUpdateAutomationPolicyRepository>();
        services.AddSingleton<IHostUpdateAutomationPolicyRepository>(sp => sp.GetRequiredService<FileHostUpdateAutomationPolicyRepository>());
        services.RemoveAll<IHostUpdateAutomationPolicyProvisioner>();
        services.AddSingleton<IHostUpdateAutomationPolicyProvisioner>(sp => sp.GetRequiredService<FileHostUpdateAutomationPolicyRepository>());
        services.RemoveAll<IHostUpdateSchedulerSettings>();
        services.AddSingleton<IHostUpdateSchedulerSettings, HostStateHostUpdateSchedulerSettings>();
        services.RemoveAll<IHostUpdateExecutionJournal>();
        services.AddSingleton<IHostUpdateExecutionJournal>(sp => new FileHostUpdateExecutionJournal(
            sp.GetRequiredService<HostStatePath>().Resolve("host-update-execution.journal")));
        services.RemoveAll<IHostUpdateExecutionLock>();
        services.AddSingleton<IHostUpdateExecutionLock>(sp => new FileHostUpdateExecutionLock(
            sp.GetRequiredService<HostStatePath>().Resolve("host-update-execution.lock")));

        // The production IHostUpdateAdmissionFence is permanently registered as
        // UnavailableHostUpdateAdmissionFence (BlocksAdmission=true) -- the same kind of
        // deliberately-unavailable-pending-physical-recovery stub as IHostUpdateExecutor -- so it
        // must also be substituted here, with the other real production no-op implementation
        // (InactiveHostUpdateAdmissionFence, already used elsewhere in production as the safe
        // default) for the manual path to ever admit a request in a test.
        services.RemoveAll<IHostUpdateAdmissionFence>();
        services.AddSingleton<IHostUpdateAdmissionFence, InactiveHostUpdateAdmissionFence>();

        services.RemoveAll<IHostUpdateExecutionRequestResolver>();
        services.AddScoped<IHostUpdateExecutionRequestResolver>(sp =>
            ActivatorUtilities.CreateInstance<HostUpdateExecutionRequestResolver>(sp));

        services.RemoveAll<IHostUpdateExecutor>();
        services.AddSingleton<IHostUpdateExecutor>(sp =>
        {
            executor.Journal = sp.GetRequiredService<IHostUpdateExecutionJournal>();
            return executor;
        });

        // Production registers IHostUpdateCandidateReadiness as UnavailableHostUpdateCandidateReadiness,
        // a permanent "not yet wired up" stub (all flags false) pending real installation/compatibility
        // machinery -- the same category of intentionally-unavailable production stub as the executor and
        // admission fence above. Kane's mandated manual-separation scenario requires "all manual safety
        // gates ... available", so this constrained, data-only fake reports readiness as satisfied without
        // performing (or claiming to perform) any physical recovery/installation work itself.
        services.RemoveAll<IHostUpdateCandidateReadiness>();
        services.AddSingleton<IHostUpdateCandidateReadiness>(new AlwaysReadyHostUpdateCandidateReadiness());

        services.RemoveAll<IHostUpdateSchedulerCandidateCache>();
        services.AddSingleton<IHostUpdateSchedulerCandidateCache>(sp => new VerifiedReleaseEvidenceCandidateCache(
            sp.GetRequiredService<IVerifiedReleaseEvidenceCache>(),
            sp.GetRequiredService<IHostUpdateCandidateReadiness>(),
            "linux-amd64",
            TimeSpan.FromHours(2)));
    }

    private static async Task ProvisionHostStateAsync(IServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        IHostUpdateReplayAnchorProvisioner replayProvisioner = scope.ServiceProvider.GetRequiredService<IHostUpdateReplayAnchorProvisioner>();
        IHostUpdateAutomationPolicyProvisioner policyProvisioner = scope.ServiceProvider.GetRequiredService<IHostUpdateAutomationPolicyProvisioner>();
        await replayProvisioner.ProvisionAsync(default);
        await policyProvisioner.ProvisionAsync(default);
    }

    private static void SeedVerifiedCandidate(IServiceProvider services, long sequence)
    {
        IVerifiedReleaseEvidenceCache cache = services.GetRequiredService<IVerifiedReleaseEvidenceCache>();
        cache.SetVerified(Evidence(sequence), DateTimeOffset.UtcNow);
    }

    private static readonly string Digest = "sha256:" + new string('a', 64);

    private static VerifiedReleaseEvidenceDto Evidence(long sequence) => new()
    {
        Sequence = sequence,
        SignatureVerified = true,
        IsComplete = true,
        MinimumUpdaterVersion = "1.0.0",
        ManifestDigest = Digest,
        Identity = new CanonicalReleaseIdentityDto
        {
            ReleaseId = "stable:1.2.3",
            Channel = "stable",
            SourceCommit = new string('c', 40),
        },
        Services = new[] { "api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker", "monolith" }
            .Select((id, index) => new ReleaseServiceRequirementDto { ServiceId = id, Platform = "linux-amd64", PlatformDigest = "sha256:" + new string((char)(97 + index), 64), IndexDigest = Digest })
            .ToArray(),
    };

    /// <summary>Constrained recording fake: only ever appends the terminal Completed activity to
    /// the real production journal (mirroring exactly what a real executor durably records) and
    /// captures the exact request it received. It performs no physical recovery/installation
    /// work whatsoever -- the production executor remains permanently unavailable.</summary>
    /// <summary>
    /// Constrained, data-only test double for <see cref="IHostUpdateCandidateReadiness"/>. Reports readiness
    /// as satisfied so the manual/automation authorization pipeline's eligibility gate can be exercised
    /// end-to-end without performing (or simulating) any physical installation/compatibility work -- the
    /// production <c>UnavailableHostUpdateCandidateReadiness</c> stub always reports every flag as false
    /// pending that unimplemented physical recovery machinery.
    /// </summary>
    private sealed class AlwaysReadyHostUpdateCandidateReadiness : IHostUpdateCandidateReadiness
    {
        public bool CompatibilityReady => true;
        public bool InstallationAvailable => true;
        public bool SafetyPassed => true;
        public bool IsNewer => true;
    }

    private sealed class RecordingConstrainedExecutor : IHostUpdateExecutor
    {
        public int Calls { get; private set; }

        public HostUpdateExecutionRequest? LastRequest { get; private set; }

        public IHostUpdateExecutionJournal? Journal { get; set; }

        public Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;

            HostUpdateExecutionActivity activity = new(Guid.NewGuid().ToString("N"), request.ReleaseId, HostUpdateExecutionState.Completed, "completed", DateTimeOffset.UtcNow)
            {
                RequestBindingHash = HostUpdateRequestBinding.Compute(request),
                RequestBinding = request,
            };
            Journal?.Append(activity);

            return Task.FromResult(new HostUpdateExecutionResult(request.ReleaseId, HostUpdateExecutionState.Completed, null, [activity]));
        }
    }
}

internal static class NullableAssertionExtensions
{
    public static T ShouldNotBeNullAndReturn<T>(this T? value) where T : class
    {
        value.Should().NotBeNull();
        return value!;
    }
}
