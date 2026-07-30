using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.Workers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Regression coverage for issue #954: the bulk <c>POST /api/settings</c> reflection-unwrap
/// <c>catch</c> used to build its error response by hand — top-level <c>message = "Validation failed"</c>
/// and <c>errors[string.Empty] = vex.Message</c> — so a memberless <see cref="ValidationException"/>
/// thrown while saving lost its concrete reason (buried under a key nothing looks up). The fix routes
/// this path through the shared <c>BuildValidationErrorResponse</c> helper, matching the inline and
/// per-key paths.
/// </summary>
/// <remarks>
/// This is a unit test rather than an HTTP integration test on purpose. The outer catch only fires for
/// a <see cref="ValidationException"/> raised by <c>ISettingsService.Save</c> (invoked via reflection);
/// the real <c>SettingsService.Save</c> never throws one, and any invalid value on an
/// <c>IValidatableSetting</c> is caught earlier by the inline <c>Validate()</c> path. A mocked
/// <c>ISettingsService</c> whose <c>Save</c> throws is the only way to exercise this fallback. A
/// non-validatable settings type (<see cref="SignalRSettings"/>) is used so the inline validation path
/// is skipped and control reaches the Save invocation.
/// </remarks>
public class UnifiedSettingsBulkPostValidationTests
{
    private static UnifiedSettingsController CreateController(ISettingsService settings)
    {
        var monitor = new DiscoveryHeartbeatMonitorService(
            Mock.Of<IBackgroundServiceMonitor>(),
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<DiscoveryHeartbeatMonitorService>.Instance);

        return new UnifiedSettingsController(
            settings,
            monitor,
            NullLogger<UnifiedSettingsController>.Instance);
    }

    /// <summary>
    /// When <c>Save</c> throws a memberless <see cref="ValidationException"/>, the bulk POST must
    /// return a 400 whose top-level <c>message</c> is the concrete reason — not the generic
    /// "Validation failed" — and whose <c>errors</c> map keys the reason under the section being
    /// processed rather than the empty string.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_MemberlessValidationExceptionFromSave_SurfacesReasonAndAttributableKey()
    {
        const string reason = "Persist rejected: SignalR realtime endpoint is unreachable";

        var settings = new Mock<ISettingsService>();
        settings
            .Setup(s => s.Save(It.IsAny<SignalRSettings>()))
            .Throws(new ValidationException(reason));

        UnifiedSettingsController controller = CreateController(settings.Object);

        // Empty JSON object deserializes to a default (non-null) SignalRSettings. SignalRSettings is
        // not IValidatableSetting, so the inline Validate() path is skipped and the controller invokes
        // Save — which the mock throws from — driving control into the outer catch under test.
        JsonElement sectionValue = JsonSerializer.SerializeToElement(new { });
        var payload = new Dictionary<string, object>
        {
            [SignalRSettings.SectionName] = sectionValue,
        };

        ActionResult result = await controller.UpdateAsync(payload);

        BadRequestObjectResult badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;

        // The response is an anonymous object `{ message, errors }`; round-trip through JSON to inspect.
        JsonElement body = JsonSerializer.SerializeToElement(badRequest.Value);

        body.TryGetProperty("message", out JsonElement messageProp).Should().BeTrue();
        messageProp.GetString().Should().Be(reason,
            "the top-level message must carry the concrete reason, not a generic 'Validation failed'");

        body.TryGetProperty("errors", out JsonElement errorsProp).Should().BeTrue();
        errorsProp.ValueKind.Should().Be(JsonValueKind.Object);

        errorsProp.TryGetProperty(SignalRSettings.SectionName, out JsonElement sectionError).Should().BeTrue(
            "a memberless exception must be keyed under the section being processed so a consumer can attribute it");
        sectionError.GetString().Should().Be(reason);

        errorsProp.TryGetProperty(string.Empty, out _).Should().BeFalse(
            "the reason must not be keyed under the empty string, which no consumer looks up");
    }
}
