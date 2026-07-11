using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Dtos.Attention;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Attention;

/// <summary>
/// Locks the feature-local wire contract for the Attention DTOs and realtime payload. Both the
/// controller (<c>ControllerStartup</c>) and SignalR (<c>SignalRStartup</c>) JSON pipelines use
/// camelCase property naming PLUS a global PascalCase <see cref="JsonStringEnumConverter"/>.
/// Because a converter in <c>Options.Converters</c> outranks a type-level <c>[JsonConverter]</c>
/// attribute, the Attention enums pin their lowercase feature-local wire form with
/// <em>property-level</em> converters, which outrank the global converter — WITHOUT changing the
/// repository-wide PascalCase enum convention. These options mirror the real pipelines so the
/// assertions reflect the true wire form.
/// </summary>
public class AttentionWireContractTests
{
    private static JsonSerializerOptions WireOptions()
    {
        // Mirror ControllerStartup/SignalRStartup: camelCase names + a global (PascalCase)
        // string-enum converter. The DTOs' property-level converters must still win.
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    [Fact(DisplayName = "AttentionChangedPayload serializes camelCase props with lowercase changeKind")]
    public void Payload_SerializesFeatureLocalWireForm()
    {
        var occurredAt = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        var payload = new AttentionChangedPayload("failure:abc", AttentionChangeKind.Updated, occurredAt);

        string json = JsonSerializer.Serialize(payload, WireOptions());

        json.Should().Contain("\"itemId\":\"failure:abc\"");
        json.Should().Contain("\"changeKind\":\"updated\"");
        json.Should().Contain("\"occurredAt\"");
        // Regression guard: the global PascalCase enum converter must not leak through.
        json.Should().NotContain("\"Updated\"");
    }

    [Theory]
    [InlineData(AttentionChangeKind.Created, "created")]
    [InlineData(AttentionChangeKind.Updated, "updated")]
    [InlineData(AttentionChangeKind.Resolved, "resolved")]
    public void Payload_ChangeKind_SerializesLowercase(AttentionChangeKind kind, string expected)
    {
        var payload = new AttentionChangedPayload("x", kind, DateTime.UtcNow);
        string json = JsonSerializer.Serialize(payload, WireOptions());
        json.Should().Contain($"\"changeKind\":\"{expected}\"");
    }

    [Fact(DisplayName = "AttentionItemDto emits lowercase kind/severity/action wire tokens")]
    public void ItemDto_SerializesFeatureLocalLowercaseEnums()
    {
        var item = new AttentionItemDto(
            Id: "failure:1",
            Kind: AttentionKind.Failure,
            Severity: AttentionSeverity.Critical,
            PrinterId: Guid.NewGuid(),
            PrinterName: "P1",
            Title: "t",
            Detail: "d",
            OccurredAt: DateTime.UtcNow,
            Actions: new List<AttentionActionDto> { new(AttentionActionKind.Pause, "Pause", true) });

        string json = JsonSerializer.Serialize(item, WireOptions());

        json.Should().Contain("\"kind\":\"failure\"");
        json.Should().Contain("\"severity\":\"critical\"");
        json.Should().Contain("\"kind\":\"pause\"");
        // Enum tokens must be lowercase; the global PascalCase converter must not leak.
        json.Should().NotContain("\"kind\":\"Failure\"");
        json.Should().NotContain("\"severity\":\"Critical\"");
        json.Should().NotContain("\"kind\":\"Pause\"");
    }

    [Fact(DisplayName = "AttentionChangeKind round-trips from its lowercase wire token in a payload")]
    public void Payload_ChangeKind_RoundTripsFromLowercaseWire()
    {
        const string wire = "{\"itemId\":\"x\",\"changeKind\":\"resolved\",\"occurredAt\":\"2026-07-10T00:00:00Z\"}";
        AttentionChangedPayload? payload = JsonSerializer.Deserialize<AttentionChangedPayload>(wire, WireOptions());
        payload.Should().NotBeNull();
        payload!.ChangeKind.Should().Be(AttentionChangeKind.Resolved);
    }
}
