using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>
/// A disposable host-update root that satisfies <see cref="HostUpdateExecutionOptionsValidator"/>
/// (absolute, outside the OS temp directory and the working directory) plus the fake tools,
/// compose file, owned directories and SQLite file the namespace proof requires. Nothing here
/// is ever executed: the recovery paths exercised by these tests never start a process.
/// </summary>
internal sealed class CliHostFixture : IDisposable
{
    public const string ReleaseId = "stable:1.2.3";
    public const string RequestId = "request-1";

    public CliHostFixture()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("A user-local application data directory is required for this test.");
        }

        Root = Path.Combine(localData, "pf-hostupdate-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StateDirectory);

        // A host that has ever run the executor already has the lock sentinel; seeding it lets
        // Snapshot() prove read-only commands never rewrite it.
        File.WriteAllText(LockPath, "pid=0;started=seeded");
        Directory.CreateDirectory(Path.Combine(Root, "tools"));
        Directory.CreateDirectory(Path.Combine(Root, "owned"));
        Directory.CreateDirectory(HostStateRoot);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(HostStateRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        File.WriteAllText(ComposeFile, "services: {}\n");
        File.WriteAllText(DockerPath, string.Empty);
        File.WriteAllText(Sqlite3Path, string.Empty);
        File.WriteAllBytes(DatabasePath, CliHostDatabase.TemplateBytes);
        foreach (string name in OwnedDirectoryNames)
        {
            Directory.CreateDirectory(Path.Combine(Root, "owned", name));
        }
    }

    /// <summary>
    /// Provisions the default standing policy the journaled authorization records. Host-state
    /// ownership validation supports only Windows and Linux; elsewhere nothing is provisioned, so
    /// tests that need a readable policy report <c>policy_unverifiable</c> instead of the whole
    /// suite failing to initialize.
    /// </summary>
    public async Task ProvisionPolicyAsync()
    {
        if (!HostStateSupported)
        {
            return;
        }

        using FileHostUpdateAutomationPolicyRepository policy = PolicyRepository();
        await policy.ProvisionAsync(CancellationToken.None);
    }

    public static bool HostStateSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static readonly string[] OwnedDirectoryNames = ["app-data", "model-uploads", "gcode-storage", "slicer-profiles", "data-protection-keys"];

    /// <summary>The policy identity recorded by <see cref="Request"/>: the provisioned default policy.</summary>
    public static HostUpdateSchedulerSettings RecordedPolicy { get; } =
        HostStateHostUpdateSchedulerSettings.ToSchedulerSettings(new HostUpdateAutomationPolicy());

    public static string CurrentPlatform => HostUpdateHostPlatform.Current();

    public string Root { get; }

    public string HostStateRoot => Path.Combine(Root, "host-state");

    public string StateDirectory => Path.Combine(Root, "state");

    public string JournalPath => Path.Combine(StateDirectory, "journal.ndjson");

    public string LockPath => Path.Combine(StateDirectory, "execution.lock");

    public string AdmissionClosedPath => Path.Combine(StateDirectory, "admission.closed");

    // Mirrors the coordinator's per-OS file-name sanitisation (':' is only invalid on Windows).
    public string OutcomePath => Path.Combine(
        StateDirectory,
        "recovery-outcomes",
        new string([.. ReleaseId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]) + ".recovery.json");

    public string ComposeFile => Path.Combine(Root, "docker-compose.yml");

    public string DockerPath => Path.Combine(Root, "tools", "docker");

    public string Sqlite3Path => Path.Combine(Root, "tools", "sqlite3");

    public string DatabasePath => Path.Combine(Root, "farm.db");

    public IConfiguration Configuration(Action<Dictionary<string, string?>>? mutate = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HostUpdateExecution:RootDirectory"] = Root,
            ["HostUpdateExecution:ComposeFiles:0"] = ComposeFile,
            ["HostUpdateExecution:HostExecutablePaths:docker"] = DockerPath,
            ["HostUpdateExecution:HostExecutablePaths:sqlite3"] = Sqlite3Path,
            ["DB_PROVIDER"] = "sqlite",
            ["ConnectionStrings:Default"] = "Data Source=" + DatabasePath,
            ["HostUpdates:HostState:Enabled"] = "true",
            ["HostUpdates:HostState:RootPath"] = HostStateRoot,
            ["HostUpdates:HostState:WindowsSecurityAttested"] = "true",
        };
        foreach (string name in OwnedDirectoryNames)
        {
            values[$"HostUpdateExecution:OwnedDirectories:{name}"] = Path.Combine(Root, "owned", name);
        }

        mutate?.Invoke(values);
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    public static HostUpdateExecutionRequest Request(string requestId = RequestId, string? hostPlatform = null)
    {
        string platform = hostPlatform ?? CurrentPlatform;
        return new(ReleaseId, 1, TargetManifestDigest, new string('b', 40), HostUpdateExecutionChannel.Stable, Targets(platform))
        {
            RequestId = requestId,
            TrustRoot = HostUpdateTrustRoot.DefaultTrustRoot,
            PolicyRevision = RecordedPolicy.PolicyRevision,
            PolicyFingerprint = RecordedPolicy.Fingerprint,
            HostPlatform = platform,
            AuthorizationKind = HostUpdateAuthorizationKind.Manual,
        };
    }

    public const string TargetManifestDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public FileHostUpdateAutomationPolicyRepository PolicyRepository() =>
        new(HostStatePath.OpenReadOnly(new HostStateOptions { Enabled = true, RootPath = HostStateRoot, WindowsSecurityAttested = true }));

    /// <summary>Changes the standing policy after authorization, advancing its revision and fingerprint.</summary>
    public async Task ChangePolicyAsync(string channel = "stable")
    {
        using FileHostUpdateAutomationPolicyRepository repository = PolicyRepository();
        HostUpdatePolicyReadResult current = repository.Read();
        HostUpdatePolicyReadResult replaced = await repository.ReplaceAsync(
            current.Policy with { Channel = channel, InsiderAcknowledged = channel == "insider" },
            current.Policy.Revision,
            CancellationToken.None);
        replaced.Available.Should().BeTrue();
    }

    /// <summary>Records the prior verified installation; by default well before the journaled authorization.</summary>
    public void SeedInstalledState(DateTimeOffset? recordedAt = null, string releaseId = "stable:1.2.2")
    {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api"] = "sha256:" + new string('1', 64),
            ["frontend"] = "sha256:" + new string('2', 64),
            ["monolith"] = "sha256:" + new string('3', 64),
        };
        var platforms = digests.Keys.ToDictionary(k => k, _ => CurrentPlatform, StringComparer.Ordinal);
        var state = new InstalledHostState(
            releaseId,
            "sha256:" + new string('9', 64),
            digests,
            string.Join('+', digests.Keys.Order(StringComparer.Ordinal)),
            recordedAt ?? DateTimeOffset.UtcNow.AddDays(-1),
            platforms);
        File.WriteAllText(Path.Combine(StateDirectory, "installed-state.json"), JsonSerializer.Serialize(state));
    }

    /// <summary>Writes a completed backup manifest (and its files) under the configured backup root.</summary>
    public string SeedBackup(bool withFiles = true)
    {
        string releaseDirectory = new([.. ReleaseId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]);
        string run = Path.Combine(Root, "backups", releaseDirectory, "20260925000000000");
        Directory.CreateDirectory(Path.Combine(run, "database"));
        var files = new[]
        {
            new HostUpdateBackupManifestFile("database/farm.db", new string('0', 64), 4),
            new HostUpdateBackupManifestFile("owned/app-data.tar", new string('0', 64), 2),
        };
        if (withFiles)
        {
            File.WriteAllText(Path.Combine(run, "database", "farm.db"), "abcd");
        }

        var manifest = new HostUpdateBackupManifest(ReleaseId, DateTimeOffset.UtcNow.AddMinutes(-30), ["database", "app-data"], files);
        File.WriteAllText(Path.Combine(run, "manifest.json"), JsonSerializer.Serialize(manifest));
        return run;
    }

    /// <summary>
    /// Seeds a journal the way the executor writes it: an <c>accepted</c> activity carrying the
    /// authorization baseline (captured now, through the production provider) followed by
    /// <paramref name="entries"/>. Pass <paramref name="baseline"/> to journal a specific
    /// baseline, or <paramref name="withBaseline"/>=false to emulate a pre-#3047 journal.
    /// </summary>
    public void SeedJournal(
        HostUpdateExecutionRequest request,
        (HostUpdateExecutionState State, string Phase)[] entries,
        HostUpdateAuthorizationBaseline? baseline = null,
        bool withBaseline = true)
    {
        var journal = new FileHostUpdateExecutionJournal(JournalPath);
        string binding = HostUpdateRequestBinding.Compute(request);
        journal.Append(new HostUpdateExecutionActivity("activity-accepted", request.ReleaseId, HostUpdateExecutionState.Accepted, "accepted", DateTimeOffset.UtcNow)
        {
            RequestBindingHash = binding,
            RequestBinding = request,
            AuthorizationBaseline = withBaseline ? baseline ?? CurrentBaseline() : null,
        });
        int index = 0;
        foreach ((HostUpdateExecutionState state, string phase) in entries)
        {
            journal.Append(new HostUpdateExecutionActivity($"activity-{index++}", request.ReleaseId, state, phase, DateTimeOffset.UtcNow)
            {
                RequestBindingHash = binding,
                RequestBinding = request,
            });
        }
    }

    public void SeedJournal(HostUpdateExecutionRequest request, params (HostUpdateExecutionState State, string Phase)[] entries) =>
        SeedJournal(request, entries, baseline: null);

    /// <summary>The baseline the executor would journal for this host right now.</summary>
    public HostUpdateAuthorizationBaseline CurrentBaseline(IConfiguration? configuration = null)
    {
        configuration ??= Configuration();
        using ServiceProvider services = HostUpdateCli.BuildServices(configuration, TextWriter.Null);
        var provider = new HostUpdateAuthorizationBaselineProvider(
            services.GetRequiredService<IInstalledHostStateStore>(),
            services.GetRequiredService<HostUpdateExecutionOptions>(),
            DatabaseProviderConfiguration.FromConfiguration(configuration));
#pragma warning disable VSTHRD002 // Synchronous test seeding over a file-backed store.
        return provider.CaptureAsync(CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    public void SeedRecoveryRequired(HostUpdateExecutionRequest? request = null, HostUpdateAuthorizationBaseline? baseline = null, bool withBaseline = true) =>
        SeedJournal(
            request ?? Request(),
            [(HostUpdateExecutionState.Applying, "apply:before"), (HostUpdateExecutionState.RecoveryRequired, "failure")],
            baseline,
            withBaseline);

    public void SeedOutcome(HostUpdateRecoveryOutcome outcome, string detail)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OutcomePath)!);
        File.WriteAllText(
            OutcomePath,
            JsonSerializer.Serialize(new HostUpdateRecoveryOutcomeRecord(ReleaseId, outcome, detail, DateTimeOffset.UtcNow.AddMinutes(-5))));
    }

    public HostUpdateRecoveryOutcomeRecord? ReadOutcome() =>
        File.Exists(OutcomePath) ? JsonSerializer.Deserialize<HostUpdateRecoveryOutcomeRecord>(File.ReadAllText(OutcomePath)) : null;

    /// <summary>Content hash of every file under the root, used to prove a command made no writes.</summary>
    /// <remarks>The seeded <c>execution.lock</c> is included: read-only commands must not rewrite it.</remarks>
    public IReadOnlyDictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(Root, path),
                path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leaked test root is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: a leaked test root is harmless.
        }
    }

    private static HostUpdateExecutionTarget[] Targets(string platform) =>
    [
        new("api", platform, "sha256:" + new string('a', 64)),
        new("frontend", platform, "sha256:" + new string('b', 64)),
        new("slicer-host", platform, "sha256:" + new string('c', 64)),
        new("printer-discovery", platform, "sha256:" + new string('d', 64)),
        new("orcaslicer-worker", platform, "sha256:" + new string('e', 64)),
        new("monolith", platform, "sha256:" + new string('f', 64)),
    ];
}
