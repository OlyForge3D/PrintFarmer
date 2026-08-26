using Farm.Modules.Abstractions.Problems;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Farm.Modules.Abstractions.Tests;

public sealed class IdempotencyProblemDetailsTests
{
    [Fact]
    public void MalformedKey_Returns400WithCode()
    {
        BadRequestObjectResult result = IdempotencyProblemDetails.MalformedKey();

        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["code"].Should().Be(IdempotencyProblemDetails.CodeMalformedKey);
        problem.Type.Should().Be(IdempotencyProblemDetails.TypeUri);
    }

    [Fact]
    public void HashConflict_Returns409WithCode()
    {
        ConflictObjectResult result = IdempotencyProblemDetails.HashConflict();

        result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["code"].Should().Be(IdempotencyProblemDetails.CodeHashConflict);
    }

    [Fact]
    public void InProgress_Returns409WithRetryAfterExtension()
    {
        ConflictObjectResult result = IdempotencyProblemDetails.InProgress();

        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["code"].Should().Be(IdempotencyProblemDetails.CodeInProgress);
        problem.Extensions["retryAfterSeconds"].Should().Be(IdempotencyProblemDetails.RetryAfterSeconds);
    }

    [Fact]
    public void PayloadTooLarge_Returns413WithCode()
    {
        ObjectResult result = IdempotencyProblemDetails.PayloadTooLarge();

        result.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["code"].Should().Be(IdempotencyProblemDetails.CodePayloadTooLarge);
        problem.Detail.Should().Contain(IdempotencyProblemDetails.MaxBufferedRequestBytes.ToString());
    }
}

public sealed class PartsInventoryProblemDetailsTests
{
    [Fact]
    public void WrongBin_Returns409WithMismatches()
    {
        string[] mismatches = ["BIN-1", "BIN-2"];

        ObjectResult result = PartsInventoryProblemDetails.WrongBin(mismatches);

        result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["code"].Should().Be(PartsInventoryProblemDetails.WrongBinCode);
        problem.Extensions["mismatches"].Should().BeSameAs(mismatches);
    }

    [Fact]
    public void WrongBin_NullMismatches_Throws()
    {
        Action act = () => PartsInventoryProblemDetails.WrongBin<string>(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void PartMappingRequired_Returns409WithExpectedExtensions()
    {
        Guid jobId = Guid.NewGuid();
        Guid projectFileId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();

        ObjectResult result = PartsInventoryProblemDetails.PartMappingRequired(jobId, projectFileId, gcodeFileId, "map it");

        result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["code"].Should().Be(PartsInventoryProblemDetails.PartMappingRequiredCode);
        problem.Extensions["jobId"].Should().Be(jobId);
        problem.Extensions["projectFileId"].Should().Be(projectFileId);
        problem.Extensions["gcodeFileId"].Should().Be(gcodeFileId);
        problem.Extensions["guidance"].Should().Be("map it");
        problem.Detail.Should().Be("map it");
    }

    [Fact]
    public void PartMappingRequired_BlankGuidance_Throws()
    {
        Action act = () => PartsInventoryProblemDetails.PartMappingRequired(Guid.NewGuid(), Guid.NewGuid(), null, " ");

        act.Should().Throw<ArgumentException>();
    }
}
