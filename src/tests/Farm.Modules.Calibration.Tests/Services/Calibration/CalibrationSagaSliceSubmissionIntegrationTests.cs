using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Farm.Modules.Calibration.Services.Calibration;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Contracts.Libraries;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Metrics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.Calibration.Tests.Services.Calibration;

/// <summary>
/// Issue #2161 AC #6 regression coverage: <see cref="CalibrationOrchestrationSagaService.BuildSliceSubmissionBody"/>
/// used to post the saga's own, now-deleted <c>CalibrationMethodNames</c> wire name to
/// <c>POST /api/slice</c>, which the real <see cref="SliceJobController"/> validated against a
/// completely different dictionary (<see cref="CalibrationMethods"/>). The two catalogues agreed
/// on only 6 of 15 names, so 9 of the saga's own methods 400'd with
/// <c>unsupported_calibration_method</c> the moment a real slice was attempted - a failure the
/// existing <c>CalibrationOrchestrationSagaServiceTests</c> suite could never catch, because it
/// exercises the saga against an in-memory fake <see cref="ISliceSubmissionGateway"/> that never
/// runs the request body through <see cref="SliceJobController"/>'s own parsing at all.
/// </summary>
/// <remarks>
/// This test builds the exact JSON body the saga would post (via the now-<see langword="internal"/>
/// <see cref="CalibrationOrchestrationSagaService.BuildSliceSubmissionBody"/>) for every
/// <see cref="CalibrationMethod"/> value, and submits it through the real, fully-wired
/// <see cref="SliceJobController.SubmitAsync"/> - the same controller
/// <c>InternalApiSliceSubmissionGateway</c> calls in production. It never touches the saga's own
/// step machinery or gateway abstraction, so it cannot be fooled by a fake gateway that skips
/// controller-side validation.
/// </remarks>
public sealed class CalibrationSagaSliceSubmissionIntegrationTests
{
    private static readonly JsonSerializerOptions RequestBindingOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TheoryData<CalibrationMethod> AllCalibrationMethods()
    {
        var data = new TheoryData<CalibrationMethod>();
        foreach (CalibrationMethod method in Enum.GetValues<CalibrationMethod>())
        {
            data.Add(method);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllCalibrationMethods))]
    public async Task SubmitAsync_ForEverySagaCalibrationMethod_NeverRejectsAsUnsupportedWireName(CalibrationMethod method)
    {
        // Arrange: build the exact request body the saga posts for this method, from a minimal
        // recorded attempt - exactly what BuildSliceSubmissionBody sees in production.
        var attempt = new global::Farm.Infrastructure.Domain.CalibrationAttempt
        {
            Method = CalibrationMethods.ToWireName(method),
            InputJson = /*lang=json,strict*/ """{"slicerEngine":"OrcaSlicer","priority":1}""",
            SpecificationJson = /*lang=json,strict*/ "{}",
        };

        JsonObject requestBody = CalibrationOrchestrationSagaService.BuildSliceSubmissionBody(attempt, method);
        var request = requestBody.Deserialize<SubmitSliceJobRequest>(RequestBindingOptions);
        request.Should().NotBeNull(
            "the saga's own request body must always bind onto the controller's request contract");

        SliceJobController controller = CreateController(out _, out _);

        // Act
        IActionResult result = await controller.SubmitAsync(request!, CancellationToken.None);

        // Assert: this is the regression itself - the saga's wire name (from CalibrationMethods,
        // the now-single canonical vocabulary) must always be *recognized* by the controller. It
        // would previously fail here for 9 of 15 methods with "unsupported_calibration_method"
        // because the saga posted a name the controller's dictionary had never heard of.
        if (result is ObjectResult { StatusCode: StatusCodes.Status400BadRequest, Value: ProblemDetails problem })
        {
            problem.Extensions.TryGetValue("code", out object? code);
            code.Should().NotBe(
                "unsupported_calibration_method",
                $"the saga's wire name for {method} must agree with what SliceJobController parses (issue #2161)");

            // The only acceptable 400 for a catalogued-but-not-yet-implemented method is the
            // distinct "not yet slicer-supported" business rejection, never an unrecognized-name
            // rejection - and only for methods actually marked unsupported.
            code.Should().Be(
                "calibration_method_not_yet_supported",
                $"a 400 for {method} may only be the distinct not-yet-supported business rejection");
            CalibrationMethods.IsSlicerSupported(method).Should().BeFalse(
                $"{method} returned calibration_method_not_yet_supported but is marked slicer-supported");
            return;
        }

        // A method the slicer does support today must be accepted outright; any method marked
        // unsupported must have taken the 400 branch above, never fall through to here.
        CalibrationMethods.IsSlicerSupported(method).Should().BeTrue(
            $"{method} is not slicer-supported and must have been rejected with calibration_method_not_yet_supported, " +
            $"but the controller returned {result.GetType().Name} instead");
        result.Should().BeOfType<CreatedResult>(
            $"{method} is slicer-supported and its request body must produce an ordinary slice job");
    }

    private static SliceJobController CreateController(
        out Mock<ISliceJobRepository> repository,
        out Mock<ISliceJobEventService> events)
    {
        Guid userId = Guid.NewGuid();
        repository = new Mock<ISliceJobRepository>();
        _ = repository
            .Setup(instance => instance.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        events = new Mock<ISliceJobEventService>();

        Mock<IRateLimitService> rateLimit = new();
        _ = rateLimit
            .Setup(instance => instance.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicerRateLimitResult(true));

        return new SliceJobController(
            repository.Object,
            events.Object,
            NullLogger<SliceJobController>.Instance,
            new Mock<IArtifactsService>().Object,
            rateLimit.Object,
            new SliceJobMetrics(),
            new Mock<IWorkerAuthService>().Object,
            new Mock<IWorkerRepository>().Object,
            new Mock<ISlicerRegistry>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                            "Test")),
                },
            },
        };
    }
}
