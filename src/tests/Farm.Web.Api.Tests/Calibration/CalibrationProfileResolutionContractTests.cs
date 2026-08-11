using System.Text.Json;
using Farm.Infrastructure.PrinterCalibration;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// The slicer-host resolution endpoint accepts exactly three profile identifiers. These tests pin
/// that exactness, in particular that a caller can never smuggle an ownership scope into the body.
/// </summary>
public sealed class CalibrationProfileResolutionContractTests
{
    private static readonly Guid MachineId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProcessId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FilamentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void TryParseRequest_WithExactlyThreeIdentifiers_Succeeds()
    {
        using JsonDocument document = JsonDocument.Parse(
            $$"""
              {"machineProfileId":"{{MachineId}}","processProfileId":"{{ProcessId}}","filamentProfileId":"{{FilamentId}}"}
              """);

        bool parsed = CalibrationProfileResolutionContract.TryParseRequest(
            document.RootElement,
            out ResolveCalibrationProfilesRequest? request);

        _ = parsed.Should().BeTrue();
        _ = request!.MachineProfileId.Should().Be(MachineId);
        _ = request.ProcessProfileId.Should().Be(ProcessId);
        _ = request.FilamentProfileId.Should().Be(FilamentId);
    }

    [Theory]

    // A caller must never be able to assert its own ownership scope.
    [InlineData(
        """{"machineProfileId":"11111111-1111-1111-1111-111111111111","processProfileId":"22222222-2222-2222-2222-222222222222","filamentProfileId":"33333333-3333-3333-3333-333333333333","userId":"44444444-4444-4444-4444-444444444444"}""")]
    [InlineData(
        """{"machineProfileId":"11111111-1111-1111-1111-111111111111","processProfileId":"22222222-2222-2222-2222-222222222222","filamentProfileId":"33333333-3333-3333-3333-333333333333","bypassOwnership":true}""")]

    // Missing member.
    [InlineData(
        """{"machineProfileId":"11111111-1111-1111-1111-111111111111","processProfileId":"22222222-2222-2222-2222-222222222222"}""")]

    // Duplicate member.
    [InlineData(
        """{"machineProfileId":"11111111-1111-1111-1111-111111111111","machineProfileId":"11111111-1111-1111-1111-111111111111","processProfileId":"22222222-2222-2222-2222-222222222222","filamentProfileId":"33333333-3333-3333-3333-333333333333"}""")]

    // Wrong casing is not silently coerced.
    [InlineData(
        """{"MachineProfileId":"11111111-1111-1111-1111-111111111111","processProfileId":"22222222-2222-2222-2222-222222222222","filamentProfileId":"33333333-3333-3333-3333-333333333333"}""")]

    // Empty GUID is not a selection.
    [InlineData(
        """{"machineProfileId":"00000000-0000-0000-0000-000000000000","processProfileId":"22222222-2222-2222-2222-222222222222","filamentProfileId":"33333333-3333-3333-3333-333333333333"}""")]

    // Non-string and malformed values.
    [InlineData(
        """{"machineProfileId":42,"processProfileId":"22222222-2222-2222-2222-222222222222","filamentProfileId":"33333333-3333-3333-3333-333333333333"}""")]
    [InlineData(
        """{"machineProfileId":"not-a-guid","processProfileId":"22222222-2222-2222-2222-222222222222","filamentProfileId":"33333333-3333-3333-3333-333333333333"}""")]
    [InlineData(
        """{"machineProfileId":null,"processProfileId":"22222222-2222-2222-2222-222222222222","filamentProfileId":"33333333-3333-3333-3333-333333333333"}""")]

    // Non-object roots.
    [InlineData("""["11111111-1111-1111-1111-111111111111"]""")]
    [InlineData("\"11111111-1111-1111-1111-111111111111\"")]
    public void TryParseRequest_WithAnythingButTheExactContract_Fails(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);

        bool parsed = CalibrationProfileResolutionContract.TryParseRequest(
            document.RootElement,
            out ResolveCalibrationProfilesRequest? request);

        _ = parsed.Should().BeFalse();
        _ = request.Should().BeNull();
    }

    [Fact]
    public void SerializerOptions_EmitExactlyTheThreeContractMembers()
    {
        string json = JsonSerializer.Serialize(
            new ResolveCalibrationProfilesRequest(MachineId, ProcessId, FilamentId),
            CalibrationProfileResolutionContract.SerializerOptions);

        using JsonDocument document = JsonDocument.Parse(json);
        _ = document.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(CalibrationProfileResolutionContract.RequiredProperties);

        // Round-trips through the exact-match validator the slicer host applies.
        _ = CalibrationProfileResolutionContract.TryParseRequest(document.RootElement, out _)
            .Should().BeTrue();
    }

    [Fact]
    public void SerializerOptions_RejectUnknownResponseMembers()
    {
        const string payload =
            """{"machine":null,"process":null,"filament":null,"unexpected":1}""";

        Action deserialize = () => JsonSerializer.Deserialize<ResolvedCalibrationProfiles>(
            payload,
            CalibrationProfileResolutionContract.SerializerOptions);

        _ = deserialize.Should().Throw<JsonException>();
    }

    [Fact]
    public void CalibrationDiscoveryDtos_SerializeProfileEvaluationStateInCamelCase()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        CalibrationCandidateDto candidate = new();
        CalibrationContextDto context = new(candidate);

        using JsonDocument candidateJson =
            JsonDocument.Parse(JsonSerializer.Serialize(candidate, options));
        using JsonDocument contextJson =
            JsonDocument.Parse(JsonSerializer.Serialize(context, options));

        _ = candidateJson.RootElement.GetProperty("profilesEvaluated").GetBoolean()
            .Should().BeFalse();
        _ = contextJson.RootElement.GetProperty("profilesEvaluated").GetBoolean()
            .Should().BeTrue();
    }
}
