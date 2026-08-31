using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Tasks;
using Farm.Testing.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Locks the wire contract for <see cref="UserTaskDto.AnchorKind"/>,
/// <see cref="UserTaskDto.SourceKind"/>, and <see cref="ShiftPlanGroupDto.AnchorKind"/> across
/// every public enum token (issue #2246), through the REAL registered
/// <see cref="JsonSerializerOptions"/> instances resolved from this app's own DI container —
/// never a locally hand-built stand-in (mirrors the precedent set by
/// <c>SlicerHostSerializerParityTests</c> for #2238). Both the MVC options
/// (<c>ControllerStartup</c>, via <see cref="JsonOptions"/>) and the SignalR payload options
/// (<c>SignalRStartup</c>, via <see cref="JsonHubProtocolOptions"/>) apply camelCase property
/// naming PLUS a global PascalCase <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>.
/// Because a converter in <c>Options.Converters</c> outranks a type-level <c>[JsonConverter]</c>
/// attribute, <c>AnchorKind</c>/<c>SourceKind</c> pin their lowercase canonical wire form with
/// PROPERTY-level converters, which in turn outrank the global converter — without changing the
/// repository-wide PascalCase convention for <see cref="UserTaskType"/>/
/// <see cref="UserTaskStatus"/>/<see cref="UserTaskPriority"/> or any other enum. This corpus
/// asserts every public anchor/source token (including <c>Timeline</c> and <c>Attention</c>, not
/// just the <c>Unspecified</c> default exercised by the end-to-end HTTP/SignalR contract tests)
/// against both real option instances.
/// </summary>
public sealed class UserTaskDtoWireContractTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>The real, DI-registered MVC <see cref="JsonSerializerOptions"/> (<c>ControllerStartup</c>).</summary>
    private JsonSerializerOptions RealMvcOptions()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
    }

    /// <summary>The real, DI-registered SignalR payload <see cref="JsonSerializerOptions"/> (<c>SignalRStartup</c>).</summary>
    private JsonSerializerOptions RealSignalROptions()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;
    }

    private static UserTaskDto CreateDto(UserTaskAnchorKind anchorKind, UserTaskSourceKind sourceKind) => new()
    {
        Id = Guid.NewGuid(),
        TaskType = UserTaskType.Custom,
        EntityType = "Manual",
        EntityId = Guid.Empty,
        Title = "Wire contract task",
        Description = null,
        Status = UserTaskStatus.Pending,
        Priority = UserTaskPriority.Normal,
        CreatedAt = DateTime.UtcNow,
        DueAt = null,
        CompletedAt = null,
        RelatedEntityCount = 0,
        MetadataJson = null,
        AnchorKind = anchorKind,
        AnchorAtUtc = null,
        WindowStartUtc = null,
        WindowEndUtc = null,
        SourceKind = sourceKind,
        SourceId = null,
    };

    public static IEnumerable<object[]> AllAnchorKinds()
    {
        foreach (UserTaskAnchorKind value in Enum.GetValues<UserTaskAnchorKind>())
        {
            yield return new object[] { value, ExpectedAnchorToken(value) };
        }
    }

    public static IEnumerable<object[]> AllSourceKinds()
    {
        foreach (UserTaskSourceKind value in Enum.GetValues<UserTaskSourceKind>())
        {
            yield return new object[] { value, ExpectedSourceToken(value) };
        }
    }

    private static string ExpectedAnchorToken(UserTaskAnchorKind anchorKind) => anchorKind switch
    {
        UserTaskAnchorKind.Unspecified => "unspecified",
        UserTaskAnchorKind.Now => "now",
        UserTaskAnchorKind.At => "at",
        UserTaskAnchorKind.Window => "window",
        UserTaskAnchorKind.AnytimeToday => "anytimeToday",
        UserTaskAnchorKind.Timeline => "timeline",
        _ => throw new ArgumentOutOfRangeException(nameof(anchorKind), anchorKind, "Unmapped UserTaskAnchorKind token — update this wire-contract test."),
    };

    private static string ExpectedSourceToken(UserTaskSourceKind sourceKind) => sourceKind switch
    {
        UserTaskSourceKind.Unspecified => "unspecified",
        UserTaskSourceKind.Attention => "attention",
        UserTaskSourceKind.FailureIncident => "failureIncident",
        UserTaskSourceKind.Harvest => "harvest",
        UserTaskSourceKind.FilamentCoverage => "filamentCoverage",
        UserTaskSourceKind.Maintenance => "maintenance",
        UserTaskSourceKind.SpoolReorder => "spoolReorder",
        UserTaskSourceKind.PrintedPartStock => "printedPartStock",
        _ => throw new ArgumentOutOfRangeException(nameof(sourceKind), sourceKind, "Unmapped UserTaskSourceKind token — update this wire-contract test."),
    };

    [Theory(DisplayName = "UserTaskDto.AnchorKind: real MVC options serialize every public token as lowercase camelCase")]
    [MemberData(nameof(AllAnchorKinds))]
    public void UserTaskDto_AnchorKind_RealMvcOptions_SerializesLowercaseToken(UserTaskAnchorKind anchorKind, string expected)
    {
        UserTaskDto dto = CreateDto(anchorKind, UserTaskSourceKind.Unspecified);

        string json = JsonSerializer.Serialize(dto, RealMvcOptions());
        using JsonDocument document = JsonDocument.Parse(json);

        JsonContractAssertions.AssertEnumToken(document.RootElement, "anchorKind", expected);
    }

    [Theory(DisplayName = "UserTaskDto.AnchorKind: real SignalR options serialize every public token as lowercase camelCase")]
    [MemberData(nameof(AllAnchorKinds))]
    public void UserTaskDto_AnchorKind_RealSignalROptions_SerializesLowercaseToken(UserTaskAnchorKind anchorKind, string expected)
    {
        UserTaskDto dto = CreateDto(anchorKind, UserTaskSourceKind.Unspecified);

        string json = JsonSerializer.Serialize(dto, RealSignalROptions());
        using JsonDocument document = JsonDocument.Parse(json);

        JsonContractAssertions.AssertEnumToken(document.RootElement, "anchorKind", expected);
    }

    [Theory(DisplayName = "UserTaskDto.SourceKind: real MVC options serialize every public token as lowercase camelCase")]
    [MemberData(nameof(AllSourceKinds))]
    public void UserTaskDto_SourceKind_RealMvcOptions_SerializesLowercaseToken(UserTaskSourceKind sourceKind, string expected)
    {
        UserTaskDto dto = CreateDto(UserTaskAnchorKind.Unspecified, sourceKind);

        string json = JsonSerializer.Serialize(dto, RealMvcOptions());
        using JsonDocument document = JsonDocument.Parse(json);

        JsonContractAssertions.AssertEnumToken(document.RootElement, "sourceKind", expected);
    }

    [Theory(DisplayName = "UserTaskDto.SourceKind: real SignalR options serialize every public token as lowercase camelCase")]
    [MemberData(nameof(AllSourceKinds))]
    public void UserTaskDto_SourceKind_RealSignalROptions_SerializesLowercaseToken(UserTaskSourceKind sourceKind, string expected)
    {
        UserTaskDto dto = CreateDto(UserTaskAnchorKind.Unspecified, sourceKind);

        string json = JsonSerializer.Serialize(dto, RealSignalROptions());
        using JsonDocument document = JsonDocument.Parse(json);

        JsonContractAssertions.AssertEnumToken(document.RootElement, "sourceKind", expected);
    }

    [Theory(DisplayName = "ShiftPlanGroupDto.AnchorKind: real MVC options serialize every public token as lowercase camelCase")]
    [MemberData(nameof(AllAnchorKinds))]
    public void ShiftPlanGroupDto_AnchorKind_RealMvcOptions_SerializesLowercaseToken(UserTaskAnchorKind anchorKind, string expected)
    {
        var group = new ShiftPlanGroupDto(anchorKind, Array.Empty<UserTaskDto>());

        string json = JsonSerializer.Serialize(group, RealMvcOptions());
        using JsonDocument document = JsonDocument.Parse(json);

        JsonContractAssertions.AssertEnumToken(document.RootElement, "anchorKind", expected);
    }

    [Theory(DisplayName = "ShiftPlanGroupDto.AnchorKind: real SignalR options serialize every public token as lowercase camelCase")]
    [MemberData(nameof(AllAnchorKinds))]
    public void ShiftPlanGroupDto_AnchorKind_RealSignalROptions_SerializesLowercaseToken(UserTaskAnchorKind anchorKind, string expected)
    {
        var group = new ShiftPlanGroupDto(anchorKind, Array.Empty<UserTaskDto>());

        string json = JsonSerializer.Serialize(group, RealSignalROptions());
        using JsonDocument document = JsonDocument.Parse(json);

        JsonContractAssertions.AssertEnumToken(document.RootElement, "anchorKind", expected);
    }

    /// <summary>The global enum policy is unchanged for enums other than AnchorKind/SourceKind — proven against the real MVC options.</summary>
    [Fact(DisplayName = "UserTaskDto: real MVC options preserve PascalCase output for unrelated enums")]
    public void UserTaskDto_RealMvcOptions_UnrelatedEnumsRemainPascalCase()
    {
        UserTaskDto dto = CreateDto(UserTaskAnchorKind.Now, UserTaskSourceKind.Maintenance) with
        {
            TaskType = UserTaskType.MaintenanceDue,
            Status = UserTaskStatus.Completed,
            Priority = UserTaskPriority.High,
        };

        string json = JsonSerializer.Serialize(dto, RealMvcOptions());
        using JsonDocument document = JsonDocument.Parse(json);

        JsonContractAssertions.AssertEnumToken(document.RootElement, "taskType", "MaintenanceDue");
        JsonContractAssertions.AssertEnumToken(document.RootElement, "status", "Completed");
        JsonContractAssertions.AssertEnumToken(document.RootElement, "priority", "High");
        JsonContractAssertions.AssertEnumToken(document.RootElement, "anchorKind", "now");
        JsonContractAssertions.AssertEnumToken(document.RootElement, "sourceKind", "maintenance");
    }
}
