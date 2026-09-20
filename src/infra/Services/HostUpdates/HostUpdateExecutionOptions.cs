namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Production configuration for the concrete host-update executor step adapters (issue
/// #2663). Every path/timeout the adapters need is bound here rather than hard-coded, so an
/// operator can point the executor at their actual deployment topology (compose files,
/// container names, health endpoints) without code changes. Defaults are conservative and
/// intentionally match the existing <c>docker-compose.daily-registry.yml</c> split-topology
/// template; a monolith deployment must override <see cref="ComposeFiles"/> and
/// <see cref="ComposeProjectName"/> accordingly.
/// </summary>
public sealed class HostUpdateExecutionOptions
{
    public const string SectionName = "HostUpdateExecution";

    /// <summary>
    /// Absolute, host-controlled, persistent root directory that owns all executor state:
    /// installed-state, the hash-chained journal, the execution lock, and coordinated backups.
    /// Deliberately outside the application database and any cache/temp path, so a container
    /// rebuild, an application-DB restore, or OS temp-directory cleanup can never silently
    /// destroy update history or in-flight recovery evidence. Must be configured explicitly
    /// (e.g. <c>HostUpdateExecution__RootDirectory</c>); there is no usable default, because any
    /// default risks silently resolving under the working directory or a container ephemeral
    /// layer. See <see cref="HostUpdateExecutionOptionsValidator"/> for the exact rules enforced
    /// at startup (rooted path; not under the OS temp directory; not the current directory).
    /// </summary>
    public string RootDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Explicit absolute paths to the host tools used by update adapters. Bare executable names
    /// and ambient PATH lookup are never accepted by the process boundary.
    /// </summary>
    public IDictionary<string, string> HostExecutablePaths { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Durable state root (installed-state, journal, lock files): <c>{RootDirectory}/state</c>.</summary>
    public string StateDirectory => Combine("state");

    /// <summary>Root directory for coordinated, checksummed backups: <c>{RootDirectory}/backups</c>.</summary>
    public string BackupRootDirectory => Combine("backups");

    /// <summary>Path whose volume/drive is checked for free space during preflight.</summary>
    public string DiskWatchPath => RootDirectory;

    private string Combine(string child) =>
        string.IsNullOrWhiteSpace(RootDirectory) ? throw new InvalidOperationException("root_directory_not_configured") : Path.Combine(RootDirectory, child);

    /// <summary>Minimum free bytes required on <see cref="DiskWatchPath"/>'s volume for preflight to pass.</summary>
    public long MinimumFreeBytes { get; set; } = 2_000_000_000;

    /// <summary>
    /// EF Core provider names (<c>DbContext.Database.ProviderName</c>) this executor is allowed
    /// to migrate. Any installed provider outside this allowlist fails preflight closed.
    /// </summary>
    public string[] SupportedProviderNames { get; set; } =
    [
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        "Microsoft.EntityFrameworkCore.SqlServer",
    ];

    /// <summary>Bounded wait for active prints/pending outbox commands to finish naturally during drain.</summary>
    public int DrainTimeoutSeconds { get; set; } = 300;

    public int DrainPollIntervalSeconds { get; set; } = 5;

    /// <summary>Bounded wait to prove every registered writer has actually quiesced before backup.</summary>
    public int FenceProofTimeoutSeconds { get; set; } = 60;

    public int FencePollIntervalSeconds { get; set; } = 2;

    /// <summary>Timeout for each provider-native backup/restore tool invocation.</summary>
    public int BackupTimeoutSeconds { get; set; } = 900;

    /// <summary>Timeout for each digest-pinned target-image migration command.</summary>
    public int MigrationTimeoutSeconds { get; set; } = 900;

    /// <summary>Bounded wait for every readiness/digest health check to report healthy.</summary>
    public int VerifyTimeoutSeconds { get; set; } = 300;

    public int VerifyPollIntervalSeconds { get; set; } = 5;

    /// <summary>Timeout for the <c>docker compose up -d</c> apply invocation.</summary>
    public int ApplyTimeoutSeconds { get; set; } = 300;

    /// <summary>Timeout for short diagnostic process invocations (docker version, docker inspect).</summary>
    public int ProcessDefaultTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// True when the application database (AppDbContext/SlicerDbContext's underlying instance)
    /// is a customer-managed external server this host does not own. When true, the backup
    /// step fails closed (<see cref="HostUpdateBackupUnsupportedOwnerException"/>) instead of
    /// silently skipping the database.
    /// </summary>
    public bool DatabaseExternallyOwned { get; set; }

    /// <summary>
    /// Production facilities that must stay fail-closed when a deployment intentionally
    /// disables them by configuration. The built-in executor no longer seeds known #2663
    /// gaps here; availability is instead determined by concrete root, adapter, writer,
    /// backup, runtime, health, and journal probes.
    /// </summary>
    public string[] RequiredUnavailableFacilities { get; set; } = [];

    /// <summary>
    /// Active compose services for this host topology. The resolver authenticates the full staged
    /// artifact set, but apply/verify only target services actually running in this topology.
    /// </summary>
    public string[] ActiveServiceIds { get; set; } =
    [
        "api",
        "frontend",
        "slicer-host",
        "printer-discovery",
        "orcaslicer-worker",
    ];

    /// <summary>
    /// Writer names (matching <see cref="IFenceableWriter.Name"/>) that must all be present in
    /// the registered fence coordinator before the executor is considered available. Backfills
    /// coverage over time as more background writers are fenced (issue #2663); an entry here
    /// with no corresponding registered <see cref="IFenceableWriter"/> makes the executor
    /// explicitly <see cref="HostUpdateExecutionAvailabilityState.Unavailable"/> rather than
    /// silently proceeding to fence only whatever happens to be registered.
    /// </summary>
    public string[] RequiredFencedWriterNames { get; set; } =
    [
        "api-admission",
        "queue-outbox-publisher",
        "power-reading-prune",
        "queue-retention-prune",
        "backend-start-command-consumer",
        "backend-control-command-consumer",
        "bed-clear-acknowledgement-expiry",
        "auto-dispatch",
        "webhook-delivery",
    ];

    /// <summary>
    /// Application-owned directories to back up (name to absolute path), matching this host's
    /// actual container mount points (<c>/data</c>, <c>/app/models</c>, <c>/app/gcode</c>,
    /// <c>/app/profiles</c>, <c>/app/data-protection-keys</c> for the compose-managed API
    /// container -- see <c>docker-compose.yml</c>). A directory is fail-closed by default: a
    /// configured path that does not exist at backup time is treated as a misconfiguration
    /// (missing mount) and fails the backup step closed, never silently masked as ".empty". Only
    /// a directory named in <see cref="OptionalOwnedDirectories"/> may legitimately be absent
    /// (e.g. certs/keyrings not configured in this deployment); for those, an absent source
    /// directory is still recorded as empty, not an error.
    /// </summary>
    public IDictionary<string, string> OwnedDirectories { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["app-data"] = "/data",
        ["model-uploads"] = "/app/models",
        ["gcode-storage"] = "/app/gcode",
        ["slicer-profiles"] = "/app/profiles",
        ["data-protection-keys"] = "/app/data-protection-keys",
    };

    /// <summary>
    /// Names (keys of <see cref="OwnedDirectories"/>) that may legitimately be absent in a given
    /// deployment without failing the backup step closed (e.g. an optional certs/keyrings
    /// directory not configured on this host). Empty by default: every current
    /// <see cref="OwnedDirectories"/> default entry is a real, always-mounted path and must be
    /// present for the backup to be considered complete.
    /// </summary>
    public string[] OptionalOwnedDirectories { get; set; } = [];

    /// <summary>Compose files (in <c>-f</c> order) applied for the currently configured topology. Must
    /// define every compose service named in <see cref="ServiceMappings"/> that this deployment
    /// actually runs; an unmapped or file-absent service fails the apply step closed.
    /// </summary>
    public string[] ComposeFiles { get; set; } = ["scripts/docker/compose-templates/docker-compose.daily-registry.yml"];

    public string ComposeProjectName { get; set; } = "printfarmer";

    /// <summary>
    /// Required entries inside the API <c>/health</c> aggregate result. Missing or non-Healthy
    /// entries fail verification closed so a top-level healthy-looking response cannot hide a
    /// skipped subsystem probe.
    /// </summary>
    public string[] RequiredAggregateHealthResultNames { get; set; } = ["comprehensive", "signalr", "spoolman"];

    /// <summary>Base URL used for HTTP readiness checks (API/nginx) during verify.</summary>
    public string HealthCheckBaseUrl { get; set; } = "http://localhost:5245";

    /// <summary>
    /// Maps each canonical release service id to its compose service name, pinned-image
    /// environment variable, and immutable image repository. Defaults match the split-topology
    /// registry deployment; override per-topology as needed.
    /// </summary>
    public HostUpdateServiceMappingOptions[] ServiceMappings { get; set; } =
    [
        new("api", "api", "PRINTFARMER_API_IMAGE", "ghcr.io/olyforge3d/printfarmer-api"),
        new("frontend", "frontend", "PRINTFARMER_FRONTEND_IMAGE", "ghcr.io/olyforge3d/printfarmer-frontend"),
        new("slicer-host", "slicer-host", "PRINTFARMER_SLICER_HOST_IMAGE", "ghcr.io/olyforge3d/printfarmer-slicer-host"),
        new("printer-discovery", "printer-discovery", "PRINTFARMER_PRINTER_DISCOVERY_IMAGE", "ghcr.io/olyforge3d/printfarmer-printer-discovery"),
        new("orcaslicer-worker", "orcaslicer-worker", "PRINTFARMER_ORCASLICER_WORKER_IMAGE", "ghcr.io/olyforge3d/printfarmer-orcaslicer-worker"),
        new("monolith", "printfarmer", "PRINTFARMER_IMAGE", "ghcr.io/olyforge3d/printfarmer-monolith"),
    ];
}

/// <summary>One entry of <see cref="HostUpdateExecutionOptions.ServiceMappings"/> (POCO for options binding).</summary>
public sealed class HostUpdateServiceMappingOptions
{
    public HostUpdateServiceMappingOptions()
    {
    }

    public HostUpdateServiceMappingOptions(string serviceId, string composeServiceName, string imageEnvironmentVariable, string imageRepository)
    {
        ServiceId = serviceId;
        ComposeServiceName = composeServiceName;
        ImageEnvironmentVariable = imageEnvironmentVariable;
        ImageRepository = imageRepository;
    }

    public string ServiceId { get; set; } = string.Empty;

    public string ComposeServiceName { get; set; } = string.Empty;

    public string ImageEnvironmentVariable { get; set; } = string.Empty;

    public string ImageRepository { get; set; } = string.Empty;
}
