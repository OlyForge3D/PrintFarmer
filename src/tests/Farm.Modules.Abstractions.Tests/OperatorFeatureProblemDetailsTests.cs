using Farm.Modules.Abstractions.Problems;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Farm.Modules.Abstractions.Tests;

public sealed class OperatorFeatureProblemDetailsTests
{
    [Fact]
    public void Create_SetsExpectedShapeAndExtensions()
    {
        ProblemDetails problem = OperatorFeatureProblemDetails.Create("attentionEnabled");

        problem.Status.Should().Be(StatusCodes.Status404NotFound);
        problem.Type.Should().Be(OperatorFeatureProblemDetails.TypeUri);
        problem.Detail.Should().Contain("attentionEnabled");
        problem.Extensions["code"].Should().Be(OperatorFeatureProblemDetails.CodeExtension);
        problem.Extensions["feature"].Should().Be("attentionEnabled");
    }

    [Fact]
    public void Create_CustomDetail_OverridesDefault()
    {
        ProblemDetails problem = OperatorFeatureProblemDetails.Create("attentionEnabled", "custom detail");

        problem.Detail.Should().Be("custom detail");
    }

    [Fact]
    public void NotFound_WrapsProblemDetailsAs404()
    {
        NotFoundObjectResult result = OperatorFeatureProblemDetails.NotFound("attentionEnabled");

        result.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        result.Value.Should().BeOfType<ProblemDetails>();
    }

    [Fact]
    public void Create_BlankFlagName_Throws()
    {
        Action act = () => OperatorFeatureProblemDetails.Create(" ");

        act.Should().Throw<ArgumentException>();
    }
}
