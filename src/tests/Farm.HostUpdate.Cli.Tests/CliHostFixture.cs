using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Extensions.Configuration;

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
        // The options binder appends configured ComposeFiles to the code default instead of
        // replacing it, so the default working-directory-relative template must also exist for
        // the namespace proof (and the engine) to see a complete compose set.
        string defaultCompose = Path.GetFullPath(new HostUpdateExecutionOptions().ComposeFiles[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(defaultCompose)!);
        if (!File.Exists(defaultCompose))
        {
            File.WriteAllText(defaultCompose, "services: {}\n");
        }

        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("A user-local application data directory is required for this test.");
        }

        Root = Path.Combine(localData, "pf-hostupdate-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(Path.Combine(Root, "tools"));
        Directory.CreateDirectory(Path.Combine(Root, "owned"));
        File.WriteAllText(ComposeFile, "services: {}\n");
        File.WriteAllText(DockerPath, string.Empty);
        File.WriteAllText(Sqlite3Path, string.Empty);
        File.WriteAllText(DatabasePath, string.Empty);
        foreach (string name in OwnedDirectoryNames)
        {
            Directory.CreateDirectory(Path.Combine(Root, "owned", name));
        }
    }

    public static readonly string[] OwnedDirectoryNames = ["app-data", "model-uploads", "gcode-storage", "slicer-profiles", "data-protection-keys"];

    public string Root { get; }

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
        };
        foreach (string name in OwnedDirectoryNames)
        {
            values[$"HostUpdateExecution:OwnedDirectories:{name}"] = Path.Combine(Root, "owned", name);
        }

        mutate?.Invoke(values);
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    public static HostUpdateExecutionRequest Request(string requestId = RequestId) =>
        new(ReleaseId, 1, "sha256:" + new string('a', 64), new string('b', 40), HostUpdateExecutionChannel.Stable, Targets())
        {
            RequestId = requestId,
            TrustRoot = "trust-root",
            PolicyRevision = 1,
            PolicyFingerprint = "policy",
            HostPlatform = "linux-amd64",
            AuthorizationKind = HostUpdateAuthorizationKind.Manual,
        };

    public void SeedJournal(HostUpdateExecutionRequest request, params (HostUpdateExecutionState State, string Phase)[] entries)
    {
        var journal = new FileHostUpdateExecutionJournal(JournalPath);
        string binding = HostUpdateRequestBinding.Compute(request);
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

    public void SeedRecoveryRequired(HostUpdateExecutionRequest? request = null) =>
        SeedJournal(
            request ?? Request(),
            (HostUpdateExecutionState.Applying, "apply:before"),
            (HostUpdateExecutionState.RecoveryRequired, "failure"));

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
    /// <remarks>The <c>execution.lock</c> sentinel is excluded: acquiring the lock creates it, and it carries no state.</remarks>
    public IReadOnlyDictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, LockPath, StringComparison.Ordinal))
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

    private static HostUpdateExecutionTarget[] Targets() =>
    [
        new("api", "linux-amd64", "sha256:" + new string('a', 64)),
        new("frontend", "linux-amd64", "sha256:" + new string('b', 64)),
        new("slicer-host", "linux-amd64", "sha256:" + new string('c', 64)),
        new("printer-discovery", "linux-amd64", "sha256:" + new string('d', 64)),
        new("orcaslicer-worker", "linux-amd64", "sha256:" + new string('e', 64)),
        new("monolith", "linux-amd64", "sha256:" + new string('f', 64)),
    ];
}
