using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.HostUpdates.PullApproval;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates.PullApproval;

public sealed class HostUpdatePullContractTests
{
    private static readonly string InstallationId = new('1', 32);
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] ForbiddenMemberFragments =
        ["image", "command", "compose", "path", "url", "uri", "script", "token", "secret", "password", "credential", "environment", "volume", "mount"];

    [Fact]
    public void Readiness_NeverGrantsExecution_EvenWhenEveryConditionIsSatisfied()
    {
        HostUpdateReadinessDto readiness = HostUpdatePullReadinessEvaluator.Evaluate(ReadinessContext());

        readiness.Eligible.Should().BeTrue();
        readiness.GrantsExecution.Should().BeFalse();
        readiness.Policy.AutomaticUpdatesRuntimeEnabled.Should().BeFalse();

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(readiness, HostUpdateDaemonJson.Options));
        json.RootElement.GetProperty("grantsExecution").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Readiness_UnknownCondition_IsNotEligible()
    {
        HostUpdateReadinessDto readiness = HostUpdatePullReadinessEvaluator.Evaluate(ReadinessContext() with
        {
            PrinterActivity = new HostUpdateReadinessConditionDto { State = HostUpdateReadinessState.Unknown, Reasons = ["printer_state_unknown"] }
        });

        readiness.Eligible.Should().BeFalse();
        readiness.Reasons.Should().Contain("printer_activity_blocking");
    }

    [Fact]
    public void Readiness_UnreadablePolicy_IsUnknownAndNotEligible()
    {
        HostUpdateReadinessDto readiness = HostUpdatePullReadinessEvaluator.Evaluate(ReadinessContext() with { PolicyReadable = false });

        readiness.Policy.State.Should().Be(HostUpdateReadinessState.Unknown);
        readiness.Eligible.Should().BeFalse();
    }

    [Fact]
    public void Readiness_UnknownOrRevokedHost_IsNotEligible()
    {
        HostUpdatePullReadinessContext context = ReadinessContext();

        HostUpdatePullReadinessEvaluator.Evaluate(context with { Enrollment = null }).Eligible.Should().BeFalse();
        HostUpdateReadinessDto revoked = HostUpdatePullReadinessEvaluator.Evaluate(context with
        {
            Enrollment = context.Enrollment! with { State = HostUpdateDaemonEnrollmentState.Revoked }
        });
        revoked.Eligible.Should().BeFalse();
        revoked.HostEligibility.Reasons.Should().Contain(HostUpdatePullReasons.EnrollmentRevoked);
    }

    [Fact]
    public void Readiness_KillSwitch_IsNotEligible()
    {
        HostUpdatePullReadinessEvaluator.Evaluate(ReadinessContext() with { KillSwitchActive = true }).Eligible.Should().BeFalse();
    }

    [Fact]
    public void StatusReport_WithBoundedCodes_IsValid()
    {
        HostUpdateDaemonStatusReportValidator.Validate(Report(), Now).Should().BeEmpty();
    }

    [Theory]
    [InlineData("/var/lib/printfarmer/host-state")]
    [InlineData("C:\\ProgramData\\PrintFarmer")]
    [InlineData("password=hunter2")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("https://registry.example/printfarmer")]
    [InlineData("docker compose up -d")]
    public void StatusReport_WithPathSecretUrlOrCommand_IsRejected(string value)
    {
        HostUpdateDaemonStatusReportDto report = Report();

        HostUpdateDaemonStatusReportValidator.Validate(report with { DeferReasons = [value] }, Now).Should().Contain("deferReasons");
        HostUpdateDaemonStatusReportValidator.Validate(report with { RecoveryHints = [value] }, Now).Should().Contain("recoveryHints");
        HostUpdateDaemonStatusReportValidator.Validate(report with { CurrentCheckpoint = value }, Now).Should().Contain("currentCheckpoint");
        HostUpdateDaemonStatusReportValidator.Validate(report with { LastResult = report.LastResult with { ReasonCode = value } }, Now)
            .Should().Contain("lastResult.reasonCode");
        HostUpdateDaemonStatusReportValidator.Validate(report with { LastResult = report.LastResult with { ApprovalId = value } }, Now)
            .Should().Contain("lastResult.approvalId");
        HostUpdateDaemonStatusReportValidator.Validate(report with { LastResult = report.LastResult with { ReleaseId = value } }, Now)
            .Should().Contain("lastResult.releaseId");
    }

    [Fact]
    public void StatusReport_TooManyItems_IsRejected()
    {
        string[] codes = Enumerable.Repeat("waiting_for_window", HostUpdateDaemonProtocol.MaxListItems + 1).ToArray();

        HostUpdateDaemonStatusReportValidator.Validate(Report() with { DeferReasons = codes }, Now).Should().Contain("deferReasons");
    }

    [Fact]
    public void StatusReport_StaleOrMissing_IsRejected()
    {
        HostUpdateDaemonStatusReportValidator.Validate(Report() with { ReportedAt = Now.AddMinutes(-5) }, Now).Should().Contain("reportedAt");
        HostUpdateDaemonStatusReportValidator.Validate(null, Now).Should().Equal("status_report_missing");
    }

    [Fact]
    public void StatusReport_UndefinedEnum_IsRejected()
    {
        HostUpdateDaemonStatusReportValidator.Validate(Report() with { DaemonState = (HostUpdateDaemonState)99 }, Now).Should().Contain("daemonState");
    }

    public static TheoryData<string> InvalidReportFields() => new(
    [
        "daemonState",
        "executionMode",
        "hostPolicyRevision",
        "currentCheckpoint",
        "deferReasons",
        "recoveryHints",
        "reportedAt",
        "lastResult",
        "lastResult.outcome",
        "lastResult.approvalId",
        "lastResult.releaseId",
        "lastResult.reasonCode",
        "lastResult.completedAt"
    ]);

    [Theory]
    [MemberData(nameof(InvalidReportFields))]
    public void StatusReport_EachInvalidBranch_ReportsOnlyThatFieldName(string field)
    {
        HostUpdateDaemonStatusReportDto report = Report();
        HostUpdateDaemonStatusReportDto invalid = field switch
        {
            "daemonState" => report with { DaemonState = (HostUpdateDaemonState)99 },
            "executionMode" => report with { ExecutionMode = (HostUpdateDaemonExecutionMode)99 },
            "hostPolicyRevision" => report with { HostPolicyRevision = -1 },
            "currentCheckpoint" => report with { CurrentCheckpoint = "Pre Pull" },
            "deferReasons" => report with { DeferReasons = null! },
            "recoveryHints" => report with { RecoveryHints = null! },
            "reportedAt" => report with { ReportedAt = Now.AddMinutes(2) },
            "lastResult" => report with { LastResult = null! },
            "lastResult.outcome" => report with { LastResult = report.LastResult with { Outcome = (HostUpdateDaemonResultOutcome)99 } },
            "lastResult.approvalId" => report with { LastResult = report.LastResult with { ApprovalId = "short" } },
            "lastResult.releaseId" => report with { LastResult = report.LastResult with { ReleaseId = "latest" } },
            "lastResult.reasonCode" => report with { LastResult = report.LastResult with { ReasonCode = "Failed: disk full" } },
            _ => report with { LastResult = report.LastResult with { CompletedAt = Now.AddMinutes(5) } }
        };

        HostUpdateDaemonStatusReportValidator.Validate(invalid, Now).Should().Equal(field);
    }

    [Fact]
    public void StatusReportAck_SerializesAcceptanceAndFieldNamesOnly()
    {
        var ack = new HostUpdateDaemonStatusReportAckDto { ReceivedAt = Now, Accepted = false, RejectedFields = ["deferReasons"] };

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(ack, HostUpdateDaemonJson.Options));

        json.RootElement.GetProperty("accepted").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("rejectedFields").EnumerateArray().Select(e => e.GetString()).Should().Equal("deferReasons");
        AssertCamelCase(json.RootElement);
    }

    [Fact]
    public void Approval_SerializesCamelCase_WithStringEnums()
    {
        var response = new HostUpdatePullApprovalResponseDto
        {
            Decision = HostUpdatePullApprovalDecision.Approved,
            Approval = SampleApproval(),
            Reasons = []
        };

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(response, HostUpdateDaemonJson.Options));
        JsonElement root = json.RootElement;
        root.GetProperty("decision").GetString().Should().Be("Approved");
        JsonElement approval = root.GetProperty("approval");
        approval.GetProperty("targetKind").GetString().Should().Be("SignedRelease");
        approval.GetProperty("origin").GetString().Should().Be("ManualOneTime");
        approval.GetProperty("manifestDigest").GetString().Should().StartWith("sha256:");
        approval.GetProperty("authorization").GetProperty("reauthenticated").GetBoolean().Should().BeTrue();
        AssertCamelCase(root);
    }

    [Fact]
    public void Enums_SerializeAsStrings_UnderDefaultSerializerToo()
    {
        foreach (Type enumType in ContractEnumTypes())
        {
            foreach (object value in Enum.GetValues(enumType))
            {
                string json = JsonSerializer.Serialize(value, enumType);
                json.Should().Be($"\"{value}\"", because: $"{enumType.Name} must serialize as a string enum");
            }
        }
    }

    [Fact]
    public void StatusReport_RoundTrips_WithStringEnums()
    {
        string json = JsonSerializer.Serialize(Report(), HostUpdateDaemonJson.Options);

        json.Should().Contain("\"daemonState\":\"Waiting\"").And.Contain("\"executionMode\":\"Manual\"").And.Contain("\"outcome\":\"Succeeded\"");
        JsonSerializer.Deserialize<HostUpdateDaemonStatusReportDto>(json, HostUpdateDaemonJson.Options).Should().BeEquivalentTo(Report());
    }

    [Theory]
    [InlineData(typeof(HostUpdatePullApprovalDto))]
    [InlineData(typeof(HostUpdatePullApprovalResponseDto))]
    [InlineData(typeof(HostUpdateApprovalAuthorizationDto))]
    [InlineData(typeof(HostUpdateApprovalConfirmationRequestDto))]
    [InlineData(typeof(HostUpdateApprovalConfirmationDto))]
    [InlineData(typeof(HostUpdateReadinessDto))]
    [InlineData(typeof(HostUpdatePolicyReadinessDto))]
    [InlineData(typeof(HostUpdateReadinessConditionDto))]
    [InlineData(typeof(HostUpdateMaintenanceWindowReadinessDto))]
    [InlineData(typeof(HostUpdateDaemonStatusReportDto))]
    [InlineData(typeof(HostUpdateDaemonLastResultDto))]
    [InlineData(typeof(HostUpdateDaemonOperatorStatusDto))]
    [InlineData(typeof(HostUpdateDaemonEnrollmentStatusDto))]
    public void ContractDtos_HaveNoExecutableOrSecretMembers(Type dto)
    {
        foreach (PropertyInfo property in dto.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string name = property.Name.ToLowerInvariant();
            ForbiddenMemberFragments.Should().NotContain(fragment => name.Contains(fragment),
                because: $"{dto.Name}.{property.Name} must not carry images, commands, Compose, paths, URLs or credentials");
        }
    }

    [Fact]
    public void Routes_AreVersionedUnderDaemonBase_AndOperatorRouteIsAdmin()
    {
        string[] daemonRoutes =
        [
            HostUpdateDaemonApiRoutes.Enrollment,
            HostUpdateDaemonApiRoutes.Readiness,
            HostUpdateDaemonApiRoutes.Approval,
            HostUpdateDaemonApiRoutes.ApprovalConfirmation,
            HostUpdateDaemonApiRoutes.Status
        ];

        daemonRoutes.Should().OnlyContain(route => route.StartsWith("/api/host-updates/daemon/v1/", StringComparison.Ordinal));
        HostUpdateDaemonApiRoutes.OperatorStatus.Should().StartWith("/api/admin/");
    }

    [Theory]
    [InlineData("v1.4.2", "v1.5.0", -1)]
    [InlineData("v1.5.0", "v1.5.0-insider.3", 1)]
    [InlineData("v1.5.0-insider.2", "v1.5.0-insider.10", -1)]
    [InlineData("v10.0.0", "v9.9.9", 1)]
    public void ReleaseIds_OrderCanonically(string left, string right, int expected)
    {
        Math.Sign(HostUpdatePullIdentifiers.CompareReleaseIds(left, right)!.Value).Should().Be(expected);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("v1.5")]
    [InlineData("v01.5.0")]
    [InlineData("ghcr.io/olyforge3d/printfarmer:v1.5.0")]
    [InlineData(null)]
    public void NonCanonicalReleaseIds_DoNotOrder(string? value)
    {
        HostUpdatePullIdentifiers.CompareReleaseIds(value, "v1.5.0").Should().BeNull();
    }

    private static IEnumerable<Type> ContractEnumTypes() =>
        typeof(HostUpdatePullApprovalDto).Assembly.GetTypes()
            .Where(type => type.IsEnum && type.Namespace == typeof(HostUpdatePullApprovalDto).Namespace
                && (type.Name.StartsWith("HostUpdateDaemon", StringComparison.Ordinal)
                    || type.Name.StartsWith("HostUpdateApproval", StringComparison.Ordinal)
                    || type.Name.StartsWith("HostUpdatePull", StringComparison.Ordinal)
                    || type.Name == nameof(HostUpdateReadinessState)));

    private static void AssertCamelCase(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                char.IsLower(property.Name[0]).Should().BeTrue(because: $"'{property.Name}' must be camelCase");
                AssertCamelCase(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                AssertCamelCase(item);
            }
        }
    }

    private static HostUpdateDaemonStatusReportDto Report() => new()
    {
        DaemonState = HostUpdateDaemonState.Waiting,
        ExecutionMode = HostUpdateDaemonExecutionMode.Manual,
        HostPolicyRevision = 12,
        CurrentCheckpoint = null,
        LastResult = new HostUpdateDaemonLastResultDto
        {
            Outcome = HostUpdateDaemonResultOutcome.Succeeded,
            ApprovalId = "approval-000000000001",
            ReleaseId = "v1.5.0",
            CompletedAt = Now.AddHours(-1),
            ReasonCode = "completed"
        },
        DeferReasons = ["waiting_for_window"],
        RecoveryHints = [],
        ReportedAt = Now
    };

    private static HostUpdatePullApprovalDto SampleApproval() => new()
    {
        ApprovalId = "approval-000000000001",
        TargetKind = HostUpdateApprovalTargetKind.SignedRelease,
        Origin = HostUpdateApprovalOrigin.ManualOneTime,
        ReleaseId = "v1.5.0",
        ManifestDigest = "sha256:" + new string('a', 64),
        Channel = "stable",
        InstallationId = InstallationId,
        EnrollmentEpoch = 3,
        KeyId = "daemon-key-0000000001",
        PolicyRevision = 4,
        TrustRevision = 8,
        HostPolicyRevision = 12,
        TopologyFingerprint = "sha256:" + new string('b', 64),
        ConfigurationFingerprint = "sha256:" + new string('c', 64),
        SchemaFingerprint = "sha256:" + new string('d', 64),
        IssuedAt = Now,
        ExpiresAt = Now.AddMinutes(4),
        Authorization = new HostUpdateApprovalAuthorizationDto { AuthorizationRevision = 1, AuthorizedAt = Now, Reauthenticated = true }
    };

    private static HostUpdatePullReadinessContext ReadinessContext()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var satisfied = new HostUpdateReadinessConditionDto { State = HostUpdateReadinessState.Satisfied, Reasons = [] };
        return new HostUpdatePullReadinessContext
        {
            Enrollment = new HostUpdateDaemonKeyRecord
            {
                KeyId = "daemon-key-0000000001",
                InstallationId = InstallationId,
                EnrollmentEpoch = 3,
                State = HostUpdateDaemonEnrollmentState.Active,
                PublicKey = key.ExportSubjectPublicKeyInfo(),
                KeyExpiresAt = Now.AddDays(30),
                EnrollmentStateRevision = 7,
                HighWaterCounter = 10,
                HighWaterCreatedAt = Now.AddSeconds(-30),
                HighWaterRequestDigest = null
            },
            StrictInstallationId = InstallationId,
            KillSwitchActive = false,
            MaintenanceWindow = new HostUpdateMaintenanceWindowReadinessDto
            {
                State = HostUpdateReadinessState.Satisfied,
                WindowStart = Now.AddHours(-1),
                WindowEnd = Now.AddHours(1),
                Reasons = []
            },
            PrinterActivity = satisfied,
            RecoveryEvidence = satisfied,
            PolicyReadable = true,
            Channel = "stable",
            PolicyRevision = 4,
            TrustRevision = 8,
            AutomaticPolicyEnabled = false,
            Now = Now
        };
    }
}
