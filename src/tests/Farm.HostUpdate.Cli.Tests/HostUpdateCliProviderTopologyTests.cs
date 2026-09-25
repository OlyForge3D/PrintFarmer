using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>
/// Provider x topology coverage for the host-local recovery CLI (issue #3000). Every database
/// provider the engine restores (SQLite, host-local and external PostgreSQL, host-local and
/// external SQL Server) is exercised against both the split and the monolith topology through
/// the real CLI, engine registration, coordinator, restore executor, image applier and health
/// verifier. Only the two host boundaries are replaced: processes are recorded by a fake
/// <see cref="IHostUpdateProcessRunner"/> (nothing is ever executed, so no container, database
/// or printer is touched) and the API <c>/health</c> endpoint is served in-memory.
/// </summary>
public sealed class HostUpdateCliProviderTopologyTests : IDisposable, IAsyncLifetime
{
    private const string Split = "split";
    private const string Monolith = "monolith";
    private const string PostgresPassword = "not-a-real-secret-pg";
    private const string SqlServerPassword = "not-a-real-secret-sql";

    private static readonly Dictionary<string, ProviderCase> Providers = new(StringComparer.Ordinal)
    {
        ["sqlite"] = new("sqlite", null, "sqlite3", "database.sqlite3", null, null),
        ["postgres-local"] = new(
            "postgres",
            $"Host=localhost;Port=5432;Database=printfarmer;Username=pf;Password={PostgresPassword}",
            "pg_restore",
            "database.dump",
            PostgresPassword,
            "PGPASSWORD"),
        ["postgres-external"] = new(
            "postgres",
            $"Host=db.customer.example;Port=6543;Database=farm_prod;Username=pf;Password={PostgresPassword}",
            "pg_restore",
            "database.dump",
            PostgresPassword,
            "PGPASSWORD"),
        ["sqlserver-local"] = new(
            "sqlserver",
            $"Server=localhost,1433;Database=printfarmer;User Id=pf;Password={SqlServerPassword};TrustServerCertificate=True",
            "sqlcmd",
            "database.bak",
            SqlServerPassword,
            "SQLCMDPASSWORD"),
        ["sqlserver-external"] = new(
            "sqlserver",
            $"Server=sql.customer.example,14330;Database=farm_prod;User Id=pf;Password={SqlServerPassword}",
            "sqlcmd",
            "database.bak",
            SqlServerPassword,
            "SQLCMDPASSWORD"),
    };

    private readonly CliHostFixture _host = new();
    private readonly RecordingProcessRunner _processes = new(ContainerImageVariables());
    private readonly HealthState _health = new();
    private IHostUpdateFenceCoordinator? _fence;

    public HostUpdateCliProviderTopologyTests()
    {
        File.WriteAllText(ToolPath("pg_restore"), string.Empty);
        File.WriteAllText(ToolPath("sqlcmd"), string.Empty);
        File.WriteAllText(MonolithComposeFile, "services: {}\n");
    }

    public static TheoryData<string, string> Matrix
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (string provider in Providers.Keys)
            {
                data.Add(provider, Split);
                data.Add(provider, Monolith);
            }

            return data;
        }
    }

    public static TheoryData<string> Topologies => new() { Split, Monolith };

    private string MonolithComposeFile => Path.Combine(_host.Root, "docker-compose.monolith.yml");

    public void Dispose() => _host.Dispose();

    public Task InitializeAsync() => _host.ProvisionPolicyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [HostStateTheory]
    [MemberData(nameof(Matrix))]
    public async Task Status_and_preview_need_no_api_and_start_no_process(string provider, string topology)
    {
        IConfiguration configuration = SeedCoordinatedRestore(provider, topology);
        _health.Available = false;
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun status = await RunAsync(configuration, "status", "--release", CliHostFixture.ReleaseId, "--json");
        CliRun preview = await RunAsync(configuration, "recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json");

        status.ExitCode.Should().Be(HostUpdateCliExitCodes.Success, status.Output);
        Result(status).GetProperty("admissionClosed").GetBoolean().Should().BeTrue();
        preview.ExitCode.Should().Be(HostUpdateCliExitCodes.Success, preview.Output);
        JsonElement result = Result(preview);
        result.GetProperty("plan").GetProperty("kind").GetString().Should().Be("CoordinatedRestore");
        result.GetProperty("namespaceProofFailures").GetArrayLength().Should().Be(0);
        result.GetProperty("drift").GetProperty("items").GetArrayLength().Should().Be(0);
        result.GetProperty("downtime").GetProperty("affectedServices").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(Services(topology));

        _processes.Calls.Should().BeEmpty("status and preview never start a host process");
        _health.Requests.Should().Be(0, "status and preview never contact the API");
        _host.Snapshot().Should().BeEquivalentTo(before);
    }

    [HostStateTheory]
    [MemberData(nameof(Matrix))]
    public async Task Confirm_restores_the_configured_database_once_then_applies_only_the_topology_services(string provider, string topology)
    {
        IConfiguration configuration = SeedCoordinatedRestore(provider, topology);
        InstalledHostState prior = ReadInstalledState();

        CliRun run = await ConfirmAsync(configuration);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Success, run.Output);
        Result(run).GetProperty("outcome").GetString().Should().Be("RolledBack");
        Result(run).GetProperty("detail").GetString().Should().Be("coordinated_restore");

        ProviderCase database = Providers[provider];
        IReadOnlyList<ProcessCall> calls = _processes.Calls;
        ProcessCall[] restores = [.. calls.Where(call => call.FileName == RestoreToolPath(database))];
        restores.Should().ContainSingle("exactly one provider-native restore runs for the single database target");
        ProcessCall restore = restores[0];
        AssertRestoreTargetsConfiguredDatabase(provider, restore);
        restore.Arguments.Should().NotContain(argument => database.Password != null && argument.Contains(database.Password, StringComparison.Ordinal), "credentials never reach argv");
        if (database.PasswordVariable is not null)
        {
            restore.Environment.Should().ContainKey(database.PasswordVariable).WhoseValue.Should().Be(database.Password);
        }

        int restoreIndex = IndexOf(calls, restore);
        ProcessCall[] pulls = [.. calls.Where(IsPull)];
        pulls.Select(call => call.Arguments[^1]).Should().BeEquivalentTo(ExpectedImageReferences(prior));
        calls.Where(IsPull).Should().OnlyContain(call => IndexOf(calls, call) > restoreIndex, "images are re-applied only after the database is restored");
        ProcessCall composeUp = calls.Single(IsComposeUp);
        ComposeServices(composeUp).Should().BeEquivalentTo(ComposeNames(topology));
        composeUp.Arguments.Should().ContainInOrder("-f", ComposeFile(topology));

        File.ReadAllText(Path.Combine(_host.Root, "owned", "app-data", "restored.txt")).Should().Be("owned-data");
        _health.Requests.Should().BeGreaterThan(0, "the rollback is verified healthy before it is reported");
        File.Exists(_host.AdmissionClosedPath).Should().BeFalse("admission reopens only after a verified rollback");
        _host.ReadOutcome()!.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
    }

    [HostStateTheory]
    [MemberData(nameof(Matrix))]
    public async Task Duplicate_confirm_after_a_host_restart_is_a_durable_no_op(string provider, string topology)
    {
        IConfiguration configuration = SeedCoordinatedRestore(provider, topology);
        (await ConfirmAsync(configuration)).ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        _processes.Clear();

        // Every CLI invocation builds a fresh process-local service graph, exactly like a restarted host.
        CliRun again = await ConfirmAsync(configuration);

        again.ExitCode.Should().Be(HostUpdateCliExitCodes.Success, again.Output);
        Result(again).GetProperty("outcome").GetString().Should().Be("RolledBack");
        _processes.Calls.Should().BeEmpty("a completed rollback is never restored or re-applied twice");
    }

    [HostStateTheory]
    [MemberData(nameof(Matrix))]
    public async Task Fence_release_failure_is_redriven_after_restart_without_restore_or_apply(string provider, string topology)
    {
        IConfiguration configuration = SeedCoordinatedRestore(provider, topology);
        _fence = new FailingFenceCoordinator();

        CliRun failed = await ConfirmAsync(configuration);

        failed.ExitCode.Should().Be(HostUpdateCliExitCodes.FenceReleasePending, failed.Output);
        Result(failed).GetProperty("detail").GetString().Should().Be("coordinated_restore|fence_release_failed:InvalidOperationException");
        File.Exists(_host.AdmissionClosedPath).Should().BeTrue("writers stay fenced until the release succeeds");
        _processes.Calls.Count(call => call.FileName == RestoreToolPath(Providers[provider])).Should().Be(1);

        _fence = null;
        _processes.Clear();
        CliRun redriven = await ConfirmAsync(configuration);

        redriven.ExitCode.Should().Be(HostUpdateCliExitCodes.Success, redriven.Output);
        Result(redriven).GetProperty("outcome").GetString().Should().Be("RolledBack");
        _processes.Calls.Should().BeEmpty("only the idempotent fence release is re-driven");
        File.Exists(_host.AdmissionClosedPath).Should().BeFalse();
    }

    [HostStateTheory]
    [MemberData(nameof(Matrix))]
    public async Task Unavailable_api_after_apply_stops_for_an_operator_with_writers_still_fenced(string provider, string topology)
    {
        IConfiguration configuration = SeedCoordinatedRestore(provider, topology);
        _health.Available = false;

        CliRun run = await ConfirmAsync(configuration);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.NeedsOperator, run.Output);
        Result(run).GetProperty("detail").GetString().Should().Be(nameof(HostUpdateVerificationTimeoutException));
        _health.Requests.Should().BeGreaterThan(0);
        File.Exists(_host.AdmissionClosedPath).Should().BeTrue("an unverified rollback never reopens admission");
        _host.ReadOutcome()!.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
    }

    [HostStateTheory]
    [MemberData(nameof(Topologies))]
    public async Task Image_only_confirm_applies_only_the_topology_services_without_a_restore(string topology)
    {
        IConfiguration configuration = Config("sqlite", topology);
        _host.SeedInstalledState(services: Services(topology));
        _host.SeedRecoveryRequired(baseline: _host.CurrentBaseline(configuration));
        InstalledHostState prior = ReadInstalledState();

        CliRun run = await ConfirmAsync(configuration);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Success, run.Output);
        Result(run).GetProperty("detail").GetString().Should().Be("image_only_rollback");
        _processes.Calls.Should().NotContain(call => call.FileName == _host.Sqlite3Path);
        _processes.Calls.Where(IsPull).Select(call => call.Arguments[^1]).Should().BeEquivalentTo(ExpectedImageReferences(prior));
        ComposeServices(_processes.Calls.Single(IsComposeUp)).Should().BeEquivalentTo(ComposeNames(topology));
    }

    [HostStateTheory]
    [InlineData(Split, Monolith)]
    [InlineData(Monolith, Split)]
    public async Task Prior_state_from_another_topology_stops_before_any_restore_or_apply(string installedTopology, string configuredTopology)
    {
        IConfiguration configuration = Config("sqlite", configuredTopology);
        _host.SeedInstalledState(services: Services(installedTopology));
        SeedMigrationFailure(configuration);
        SeedBackup(Providers["sqlite"]);
        File.WriteAllText(_host.AdmissionClosedPath, string.Empty);

        CliRun preview = await RunAsync(configuration, "recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json");
        CliRun confirm = await ConfirmAsync(configuration);

        preview.ExitCode.Should().Be(HostUpdateCliExitCodes.NeedsOperator, preview.Output);
        Result(preview).GetProperty("plan").GetProperty("detail").GetString().Should().Be(HostUpdateRecoveryCoordinator.PriorStateTopologyMismatch);
        confirm.ExitCode.Should().Be(HostUpdateCliExitCodes.NeedsOperator, confirm.Output);
        Result(confirm).GetProperty("detail").GetString().Should().Be(HostUpdateRecoveryCoordinator.PriorStateTopologyMismatch);
        _processes.Calls.Should().BeEmpty("a topology mismatch stops before the database or any image is touched");
        File.Exists(_host.AdmissionClosedPath).Should().BeTrue();
    }

    [HostStateFact]
    public async Task Topology_changed_after_authorization_needs_reapproval_and_then_still_stops()
    {
        IConfiguration authorized = Config("sqlite", Split);
        IConfiguration current = Config("sqlite", Monolith);
        _host.SeedInstalledState(services: Services(Split));
        SeedMigrationFailure(authorized);
        SeedBackup(Providers["sqlite"]);

        CliRun unapproved = await ConfirmAsync(current);
        CliRun preview = await RunAsync(current, "recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json");
        string token = Result(preview).GetProperty("drift").GetProperty("reapprovalToken").GetString()!;
        CliRun reapproved = await RunAsync(current, "recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--reapprove-drift", token, "--json");

        unapproved.ExitCode.Should().Be(HostUpdateCliExitCodes.DriftUnapproved, unapproved.Output);
        Result(unapproved).GetProperty("details").EnumerateArray().Select(e => e.GetString()).Should().Contain(HostUpdateRecoveryDrift.ConfigurationDrift);
        reapproved.ExitCode.Should().Be(HostUpdateCliExitCodes.NeedsOperator, reapproved.Output);
        Result(reapproved).GetProperty("detail").GetString().Should().Be(HostUpdateRecoveryCoordinator.PriorStateTopologyMismatch);
        _processes.Calls.Should().BeEmpty();
    }

    [HostStateTheory]
    [InlineData("postgres-local", "HostUpdateExecution:DatabaseExternallyOwned", "true")]
    [InlineData("postgres-local", "ConnectionStrings:Default", "Host=db.customer.example;Port=5432;Database=printfarmer;Username=pf;Password=" + PostgresPassword)]
    [InlineData("postgres-external", "ConnectionStrings:Default", "Host=db.customer.example;Port=6543;Database=farm_other;Username=pf;Password=" + PostgresPassword)]
    [InlineData("sqlserver-local", "ConnectionStrings:Default", "Server=sql.customer.example,1433;Database=printfarmer;User Id=pf;Password=" + SqlServerPassword)]
    [InlineData("sqlserver-external", "HostUpdateExecution:DatabaseExternallyOwned", "true")]
    public async Task Database_retargeted_after_authorization_is_refused_before_any_process(string provider, string key, string value)
    {
        IConfiguration authorized = Config(provider, Split);
        IConfiguration retargeted = Config(provider, Split, values => values[key] = value);
        _host.SeedInstalledState();
        SeedMigrationFailure(authorized);
        SeedBackup(Providers[provider]);

        CliRun preview = await RunAsync(retargeted, "recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json");
        CliRun confirm = await ConfirmAsync(retargeted);

        DriftCodes(Result(preview)).Should().Contain(HostUpdateRecoveryDrift.ConfigurationDrift);
        confirm.ExitCode.Should().Be(HostUpdateCliExitCodes.DriftUnapproved, confirm.Output);
        Result(confirm).GetProperty("code").GetString().Should().Be("drift_reapproval_required");
        _processes.Calls.Should().BeEmpty("a retargeted database is never restored without reapproval");
        _host.ReadOutcome().Should().BeNull();
    }

    [HostStateTheory]
    [InlineData("postgres-external")]
    [InlineData("sqlserver-external")]
    public async Task Externally_owned_database_is_never_restored_by_this_host(string provider)
    {
        IConfiguration configuration = Config(provider, Split, values => values["HostUpdateExecution:DatabaseExternallyOwned"] = "true");
        _host.SeedInstalledState();
        SeedMigrationFailure(configuration);
        SeedBackup(Providers[provider]);
        File.WriteAllText(_host.AdmissionClosedPath, string.Empty);

        CliRun preview = await RunAsync(configuration, "recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json");
        CliRun confirm = await ConfirmAsync(configuration);

        Result(preview).GetProperty("namespaceProofFailures").GetArrayLength().Should().Be(0, "an owned-elsewhere database needs no host restore tool");
        confirm.ExitCode.Should().Be(HostUpdateCliExitCodes.NeedsOperator, confirm.Output);
        Result(confirm).GetProperty("detail").GetString().Should().Be(nameof(InvalidOperationException), "restore_target_unmapped fails closed");
        _processes.Calls.Should().BeEmpty("neither the external database nor any image is touched");
        File.Exists(_host.AdmissionClosedPath).Should().BeTrue();
    }

    [HostStateTheory]
    [MemberData(nameof(Topologies))]
    public async Task Missing_prior_state_after_apply_started_stops_before_any_process(string topology)
    {
        IConfiguration configuration = Config("postgres-external", topology);
        _host.SeedRecoveryRequired(baseline: _host.CurrentBaseline(configuration));
        SeedBackup(Providers["postgres-external"]);

        CliRun confirm = await ConfirmAsync(configuration);

        confirm.ExitCode.Should().Be(HostUpdateCliExitCodes.NeedsOperator, confirm.Output);
        Result(confirm).GetProperty("detail").GetString().Should().Be("prior_image_state_missing_after_apply_started");
        _processes.Calls.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Corrupt_or_missing_state_and_a_held_lock_stop_before_any_process(string provider, string topology)
    {
        IConfiguration configuration = Config(provider, topology);

        CliRun missingJournal = await ConfirmAsync(configuration);

        _host.SeedInstalledState(services: Services(topology));
        _host.SeedRecoveryRequired(baseline: _host.CurrentBaseline(configuration));
        CliRun held;
        using (new FileHostUpdateExecutionLock(_host.LockPath).Acquire(TimeSpan.FromSeconds(5), CancellationToken.None))
        {
            held = await ConfirmAsync(configuration);
        }

        string[] lines = File.ReadAllLines(_host.JournalPath);
        lines[1] = lines[1].Replace("apply:before", "apply:tampered", StringComparison.Ordinal);
        File.WriteAllLines(_host.JournalPath, lines);
        CliRun corrupt = await ConfirmAsync(configuration);

        missingJournal.ExitCode.Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven, missingJournal.Output);
        Result(missingJournal).GetProperty("details").EnumerateArray().Select(e => e.GetString()).Should().Contain("journal_missing");
        held.ExitCode.Should().Be(HostUpdateCliExitCodes.LockHeld, held.Output);
        corrupt.ExitCode.Should().Be(HostUpdateCliExitCodes.StateUnreadable, corrupt.Output);
        _processes.Calls.Should().BeEmpty();
        _host.ReadOutcome().Should().BeNull();
    }

    private static string[] Services(string topology) => topology == Monolith ? ["monolith"] : CliHostFixture.SplitServices;

    private static string[] ComposeNames(string topology) => topology == Monolith ? ["printfarmer"] : CliHostFixture.SplitServices;

    private static Dictionary<string, string> ContainerImageVariables()
    {
        var options = new HostUpdateExecutionOptions();
        return options.ServiceMappings.ToDictionary(
            mapping => $"{options.ComposeProjectName}-{mapping.ComposeServiceName}-1",
            mapping => mapping.ImageEnvironmentVariable,
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> ExpectedImageReferences(InstalledHostState prior)
    {
        HostUpdateServiceMappingOptions[] mappings = new HostUpdateExecutionOptions().ServiceMappings;
        return prior.ServiceDigests.Select(pair => $"{mappings.Single(m => m.ServiceId == pair.Key).ImageRepository}@{pair.Value}");
    }

    private static bool IsPull(ProcessCall call) => call.Arguments is ["image", "pull", ..];

    private static bool IsComposeUp(ProcessCall call) => call.Arguments is ["compose", ..];

    private static IEnumerable<string> ComposeServices(ProcessCall composeUp) =>
        composeUp.Arguments.SkipWhile(argument => argument != "never").Skip(1);

    private static int IndexOf(IReadOnlyList<ProcessCall> calls, ProcessCall call) =>
        calls.Select((candidate, index) => (candidate, index)).First(pair => ReferenceEquals(pair.candidate, call)).index;

    private static JsonElement Result(CliRun run)
    {
        using JsonDocument document = JsonDocument.Parse(run.Output);
        JsonElement root = document.RootElement.Clone();
        root.GetProperty("exitCode").GetInt32().Should().Be(run.ExitCode);
        return root.GetProperty("result");
    }

    private static string?[] DriftCodes(JsonElement result) =>
        [.. result.GetProperty("drift").GetProperty("items").EnumerateArray().Select(item => item.GetProperty("code").GetString())];

    private void AssertRestoreTargetsConfiguredDatabase(string provider, ProcessCall restore)
    {
        switch (provider)
        {
            case "sqlite":
                restore.Arguments[0].Should().Be(_host.DatabasePath);
                restore.Arguments[1].Should().StartWith(".restore '");
                break;
            case "postgres-local":
                restore.Arguments.Should().ContainInOrder("-h", "localhost", "-p", "5432", "-U", "pf", "-d", "printfarmer");
                break;
            case "postgres-external":
                restore.Arguments.Should().ContainInOrder("-h", "db.customer.example", "-p", "6543", "-U", "pf", "-d", "farm_prod");
                break;
            case "sqlserver-local":
                restore.Arguments.Should().ContainInOrder("-S", "localhost,1433", "-b", "-N", "-C", "-U", "pf");
                restore.Arguments[^1].Should().Contain("RESTORE DATABASE [printfarmer]");
                break;
            case "sqlserver-external":
                restore.Arguments.Should().ContainInOrder("-S", "sql.customer.example,14330", "-b", "-N", "-U", "pf");
                restore.Arguments.Should().NotContain("-C", "certificate validation is only waived when explicitly configured");
                restore.Arguments[^1].Should().Contain("RESTORE DATABASE [farm_prod]");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        }
    }

    private string ToolPath(string tool) => Path.Combine(_host.Root, "tools", tool);

    private string RestoreToolPath(ProviderCase database) => database.Tool == "sqlite3" ? _host.Sqlite3Path : ToolPath(database.Tool);

    private string ComposeFile(string topology) => topology == Monolith ? MonolithComposeFile : _host.ComposeFile;

    private IConfiguration Config(string provider, string topology, Action<Dictionary<string, string?>>? mutate = null) =>
        _host.Configuration(values =>
        {
            ProviderCase database = Providers[provider];
            values["DB_PROVIDER"] = database.DbProvider;
            if (database.ConnectionString is not null)
            {
                values["ConnectionStrings:Default"] = database.ConnectionString;
            }

            if (database.Tool != "sqlite3")
            {
                values[$"HostUpdateExecution:HostExecutablePaths:{database.Tool}"] = ToolPath(database.Tool);
            }

            values["HostUpdateExecution:VerifyTimeoutSeconds"] = "1";
            values["HostUpdateExecution:VerifyPollIntervalSeconds"] = "1";
            if (topology == Monolith)
            {
                values["HostUpdateExecution:ActiveServiceIds:0"] = "monolith";
                values["HostUpdateExecution:ComposeFiles:0"] = MonolithComposeFile;
            }

            mutate?.Invoke(values);
        });

    private IConfiguration SeedCoordinatedRestore(string provider, string topology)
    {
        IConfiguration configuration = Config(provider, topology);
        _host.SeedInstalledState(services: Services(topology));
        SeedMigrationFailure(configuration);
        SeedBackup(Providers[provider]);
        File.WriteAllText(_host.AdmissionClosedPath, string.Empty);
        return configuration;
    }

    private void SeedMigrationFailure(IConfiguration authorizedConfiguration) =>
        _host.SeedJournal(
            CliHostFixture.Request(),
            [(HostUpdateExecutionState.Migrating, "migration:before"), (HostUpdateExecutionState.RecoveryRequired, "failure")],
            _host.CurrentBaseline(authorizedConfiguration));

    /// <summary>A completed, checksum-valid backup of the database and one owned directory.</summary>
    private void SeedBackup(ProviderCase database)
    {
        string releaseDirectory = new([.. CliHostFixture.ReleaseId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]);
        string run = Path.Combine(_host.Root, "backups", releaseDirectory, "20260925000000000");
        var files = new List<HostUpdateBackupManifestFile>
        {
            WriteBackupFile(run, $"database/{database.DumpFile}", "database-dump"),
            WriteBackupFile(run, "app-data/restored.txt", "owned-data"),
        };
        var manifest = new HostUpdateBackupManifest(CliHostFixture.ReleaseId, DateTimeOffset.UtcNow.AddMinutes(-30), ["database", "app-data"], files);
        File.WriteAllText(Path.Combine(run, "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private static HostUpdateBackupManifestFile WriteBackupFile(string run, string relativePath, string content)
    {
        string path = Path.Combine(run, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(path, bytes);
        return new HostUpdateBackupManifestFile(relativePath, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength);
    }

    private InstalledHostState ReadInstalledState() =>
        JsonSerializer.Deserialize<InstalledHostState>(File.ReadAllText(Path.Combine(_host.StateDirectory, "installed-state.json")))!;

    private Task<CliRun> ConfirmAsync(IConfiguration configuration) =>
        RunAsync(configuration, "recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--json");

    private async Task<CliRun> RunAsync(IConfiguration configuration, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await HostUpdateCli.RunAsync(args, () => configuration, output, error, ReplaceHostBoundaries, CancellationToken.None);
        return new CliRun(exitCode, output.ToString(), error.ToString());
    }

    private void ReplaceHostBoundaries(IServiceCollection services)
    {
        services.AddSingleton<IHostUpdateProcessRunner>(_processes);
        services.AddHttpClient(HostUpdateRecoveryEngineRegistration.HealthClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HealthHandler(_health));
        if (_fence is not null)
        {
            services.AddSingleton(_fence);
        }
    }

    private sealed record CliRun(int ExitCode, string Output, string Error);

    private sealed record ProviderCase(string DbProvider, string? ConnectionString, string Tool, string DumpFile, string? Password, string? PasswordVariable);

    private sealed record ProcessCall(string FileName, string[] Arguments, IReadOnlyDictionary<string, string>? Environment);

    /// <summary>
    /// Records every host process instead of running it. <c>docker compose up</c> environments are
    /// remembered so a later <c>docker container inspect</c> reports exactly the image the
    /// recorded apply pinned, which is what the real digest verifier checks.
    /// </summary>
    private sealed class RecordingProcessRunner(IReadOnlyDictionary<string, string> imageVariableByContainer) : IHostUpdateProcessRunner
    {
        private readonly ConcurrentQueue<ProcessCall> _calls = new();
        private readonly ConcurrentDictionary<string, string> _pinnedImages = new(StringComparer.Ordinal);

        public IReadOnlyList<ProcessCall> Calls => [.. _calls];

        public void Clear() => _calls.Clear();

        public Task<HostUpdateProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            _calls.Enqueue(new ProcessCall(fileName, [.. arguments], environment is null ? null : new Dictionary<string, string>(environment, StringComparer.Ordinal)));
            if (arguments is ["compose", ..] && environment is not null)
            {
                foreach ((string variable, string image) in environment)
                {
                    _pinnedImages[variable] = image;
                }
            }

            return Task.FromResult(arguments switch
            {
                ["container", "inspect", _, _, string container] =>
                    imageVariableByContainer.TryGetValue(container, out string? variable) && _pinnedImages.TryGetValue(variable, out string? image)
                        ? new HostUpdateProcessResult(0, image, string.Empty)
                        : new HostUpdateProcessResult(1, string.Empty, "no such container"),
                ["image", "inspect", _, _, string imageReference] => new HostUpdateProcessResult(0, imageReference, string.Empty),
                _ => new HostUpdateProcessResult(0, string.Empty, string.Empty),
            });
        }
    }

    private sealed class HealthState
    {
        private int _requests;

        public volatile bool Available = true;

        public int Requests => Volatile.Read(ref _requests);

        public void Record() => Interlocked.Increment(ref _requests);
    }

    private sealed class HealthHandler(HealthState state) : HttpMessageHandler
    {
        private const string HealthyReport = """
            {"status":"Healthy","results":{"comprehensive":{"status":"Healthy"},"signalr":{"status":"Healthy"},"spoolman":{"status":"Healthy"}}}
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            state.Record();
            if (!state.Available)
            {
                throw new HttpRequestException("connection refused");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(HealthyReport) });
        }
    }

    private sealed class FailingFenceCoordinator : IHostUpdateFenceCoordinator
    {
        public Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("fence_not_expected");

        public Task ReleaseAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("fence_release_failed");
    }
}
