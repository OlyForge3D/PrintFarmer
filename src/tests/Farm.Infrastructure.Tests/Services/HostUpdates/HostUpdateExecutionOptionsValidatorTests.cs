using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Services.Queue;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Focused coverage for <see cref="HostUpdateExecutionOptionsValidator"/> (issue #2663). When
/// <c>RootDirectory</c> is configured, proves the executor can never start with its durable root
/// resolving under a temp directory or the process's working directory, and that every other
/// required field is enforced. When <c>RootDirectory</c> is left empty (the default -- no
/// supported deployment configures it yet), deployment-specific validation short-circuits
/// instead of crashing every host at startup; runtime unavailability for that case is reported
/// by <see cref="HostUpdateExecutionAvailabilityProvider"/> while the code-owned writer minimum
/// remains non-negotiable (see
/// <c>Validate_MissingRootDirectory_SucceedsAsOptionalDefaultOffFeature</c> below).
/// </summary>
public class HostUpdateExecutionOptionsValidatorTests
{
    private static readonly string[] CodeOwnedRequiredFencedWriterNames =
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
        "queue-reconciliation",
    ];

    private static readonly HostUpdateExecutionOptionsValidator Validator = new();

    private static HostUpdateExecutionOptions ValidOptions(string root) => new() { RootDirectory = root };

    private static HostUpdateExecutionOptions BindRequiredFencedWriterNames(params string[] writerNames)
    {
        string json = JsonSerializer.Serialize(new
        {
            HostUpdateExecution = new
            {
                RequiredFencedWriterNames = writerNames,
            },
        });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
        var options = new HostUpdateExecutionOptions
        {
            RequiredFencedWriterNames = [],
        };
        configuration.GetSection(HostUpdateExecutionOptions.SectionName).Bind(options);
        options.RootDirectory = Path.Combine(
            Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\",
            "printfarmer-host-updates-test-root");
        return options;
    }

    [Fact]
    public void Validate_ValidAbsoluteRootOutsideTempAndCwd_Succeeds()
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        ValidateOptionsResult result = Validator.Validate(null, ValidOptions(root));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_MissingRootDirectory_SucceedsAsOptionalDefaultOffFeature()
    {
        // Bishop/Hicks review (issue #2663): RootDirectory is optional/default-off. No supported
        // deployment shape configures it today, so requiring it unconditionally crashed every
        // host at startup the instant AddHostUpdateExecution is registered. An unconfigured root
        // must not fail process start; HostUpdateExecutionAvailabilityProvider is the runtime
        // choke point that reports root_directory_not_configured as Unavailable to callers.
        ValidateOptionsResult result = Validator.Validate(null, new HostUpdateExecutionOptions { RootDirectory = string.Empty });

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(319)]
    public void Validate_MissingRootDirectory_IgnoresFenceProofBudget(
        int fenceProofTimeoutSeconds)
    {
        var options = new HostUpdateExecutionOptions
        {
            RootDirectory = string.Empty,
            FenceProofTimeoutSeconds = fenceProofTimeoutSeconds,
        };

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_FenceProofHeadroomLessThanPollInterval_Fails()
    {
        string root = Path.Combine(
            Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\",
            "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.FenceProofTimeoutSeconds = 320;
        options.FencePollIntervalSeconds = 2;

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(
            "must exceed the required writer duration by at least FencePollIntervalSeconds (2 seconds)");
    }

    [Fact]
    public void Validate_FenceProofHeadroomEqualToPollInterval_Succeeds()
    {
        string root = Path.Combine(
            Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\",
            "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.FenceProofTimeoutSeconds = 322;
        options.FencePollIntervalSeconds = 3;

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(319)]
    public void Validate_FenceProofBudgetNotGreaterThanRequiredWriterDuration_Fails(
        int fenceProofTimeoutSeconds)
    {
        string root = Path.Combine(
            Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\",
            "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.FenceProofTimeoutSeconds = fenceProofTimeoutSeconds;

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("must be greater than 319 seconds");
    }

    [Fact]
    public void Validate_ConfiguredEmptyRequiredFencedWriterNames_FailsWithExactCodeOwnedSet()
    {
        HostUpdateExecutionOptions options = BindRequiredFencedWriterNames();
        options.RequiredFencedWriterNames.Should().BeEmpty();

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Be(
            "HostUpdateExecution:RequiredFencedWriterNames must include every code-owned required writer: "
            + string.Join(',', CodeOwnedRequiredFencedWriterNames)
            + ".");
    }

    [Fact]
    public void Validate_ConfiguredPartialRequiredFencedWriterNames_FailsWithExactMissingSet()
    {
        string[] partialSet = CodeOwnedRequiredFencedWriterNames[..^2];
        HostUpdateExecutionOptions options = BindRequiredFencedWriterNames(partialSet);
        options.RequiredFencedWriterNames.Should().Equal(partialSet);

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Be(
            "HostUpdateExecution:RequiredFencedWriterNames must include every code-owned required writer: "
            + string.Join(',', CodeOwnedRequiredFencedWriterNames[^2..])
            + ".");
    }

    [Fact]
    public void Validate_ConfiguredSetMissingExactlyOneRequiredFencedWriter_FailsWithExactMissingName()
    {
        HostUpdateExecutionOptions options = BindRequiredFencedWriterNames(
            CodeOwnedRequiredFencedWriterNames[..^1]);

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Be(
            "HostUpdateExecution:RequiredFencedWriterNames must include every code-owned required writer: "
            + CodeOwnedRequiredFencedWriterNames[^1]
            + ".");
    }

    [Fact]
    public void Validate_NullRequiredFencedWriterNames_FailsWithExactCodeOwnedSet()
    {
        HostUpdateExecutionOptions options = ValidOptions(
            Path.Combine(
                Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\",
                "printfarmer-host-updates-test-root"));
        options.RequiredFencedWriterNames = null!;

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Be(
            "HostUpdateExecution:RequiredFencedWriterNames must include every code-owned required writer: "
            + string.Join(',', CodeOwnedRequiredFencedWriterNames)
            + ".");
    }

    [Fact]
    public void Validate_CaseOnlyRequiredFencedWriterName_DoesNotSatisfyCanonicalName()
    {
        string[] caseVariantSet =
        [
            "API-ADMISSION",
            .. CodeOwnedRequiredFencedWriterNames[1..],
        ];
        HostUpdateExecutionOptions options = BindRequiredFencedWriterNames(caseVariantSet);

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Be(
            "HostUpdateExecution:RequiredFencedWriterNames must include every code-owned required writer: api-admission.");
    }

    [Fact]
    public void Validate_MissingRootDirectoryWithNarrowedFencedWriterNames_StillFails()
    {
        HostUpdateExecutionOptions options = BindRequiredFencedWriterNames(
            CodeOwnedRequiredFencedWriterNames[..^2]);
        options.RootDirectory = string.Empty;

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Be(
            "HostUpdateExecution:RequiredFencedWriterNames must include every code-owned required writer: "
            + string.Join(',', CodeOwnedRequiredFencedWriterNames[^2..])
            + ".");
    }

    [Fact]
    public void Validate_ConfiguredSupersetRequiredFencedWriterNames_Succeeds()
    {
        string[] superset = [.. CodeOwnedRequiredFencedWriterNames, "deployment-specific-writer"];
        HostUpdateExecutionOptions options = BindRequiredFencedWriterNames(superset);

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void DerivedStatePaths_MissingRootDirectory_ThrowInsteadOfResolvingRelative()
    {
        var options = new HostUpdateExecutionOptions { RootDirectory = string.Empty };

        Action state = () => _ = options.StateDirectory;
        Action backups = () => _ = options.BackupRootDirectory;

        state.Should().Throw<InvalidOperationException>().WithMessage("root_directory_not_configured");
        backups.Should().Throw<InvalidOperationException>().WithMessage("root_directory_not_configured");
    }
    [Fact]
    public void Validate_RelativeRootDirectory_Fails()
    {
        ValidateOptionsResult result = Validator.Validate(null, ValidOptions("relative/path"));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("must be an absolute path");
    }

    [Fact]
    public void Validate_RootUnderTempDirectory_Fails()
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-host-updates");
        ValidateOptionsResult result = Validator.Validate(null, ValidOptions(root));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("must not be under the OS temp directory");
    }

    [Fact]
    public void Validate_RootIsCurrentWorkingDirectory_Fails()
    {
        ValidateOptionsResult result = Validator.Validate(null, ValidOptions(Directory.GetCurrentDirectory()));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("current/working directory");
    }

    [Fact]
    public void Validate_RootIsSubdirectoryOfCurrentWorkingDirectory_Fails()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "host-updates");
        ValidateOptionsResult result = Validator.Validate(null, ValidOptions(root));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("current/working directory");
    }

    [Fact]
    public void Validate_EmptySupportedProviderNames_Fails()
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.SupportedProviderNames = [];

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("SupportedProviderNames");
    }

    [Fact]
    public void Validate_EmptyComposeFiles_Fails()
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.ComposeFiles = [];

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ComposeFiles");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankComposeFileEntry_Fails(string entry)
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.ComposeFiles = ["/opt/printfarmer/docker-compose.yml", entry];

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ComposeFiles entries must not be empty");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankActiveServiceIdEntry_Fails(string entry)
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.ActiveServiceIds = ["monolith", entry];

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ActiveServiceIds entries must not be empty");
    }

    [Fact]
    public void Validate_EmptyActiveServiceIds_Fails()
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.ActiveServiceIds = [];

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ActiveServiceIds must list at least one active service");
    }

    [Fact]
    public void Validate_DuplicateServiceMappingServiceId_Fails()
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.ServiceMappings =
        [
            new("api", "api", "PRINTFARMER_API_IMAGE", "ghcr.io/olyforge3d/printfarmer-api"),
            new("api", "api-2", "PRINTFARMER_API_IMAGE_2", "ghcr.io/olyforge3d/printfarmer-api"),
        ];

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("duplicate ServiceId: api");
    }

    [Fact]
    public void Validate_ServiceMappingMissingField_Fails()
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.ServiceMappings = [new HostUpdateServiceMappingOptions("api", string.Empty, "IMG", "repo")];

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("missing a required field");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveMinimumFreeBytes_Fails(long minimumFreeBytes)
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.MinimumFreeBytes = minimumFreeBytes;

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("MinimumFreeBytes");
    }

    [Fact]
    public void Validate_NonPositiveTimeout_Fails()
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.DrainTimeoutSeconds = 0;

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("DrainTimeoutSeconds");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveMigrationTimeout_Fails(int migrationTimeoutSeconds)
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.MigrationTimeoutSeconds = migrationTimeoutSeconds;

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("HostUpdateExecution:MigrationTimeoutSeconds must be positive.");
    }

    [Fact]
    public void Validate_InvalidHealthCheckBaseUrl_Fails()
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.HealthCheckBaseUrl = "not-a-url";

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("HealthCheckBaseUrl");
    }

    [Fact]
    public void Validate_MissingComposeProjectName_Fails()
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = ValidOptions(root);
        options.ComposeProjectName = string.Empty;

        ValidateOptionsResult result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ComposeProjectName");
    }
}
