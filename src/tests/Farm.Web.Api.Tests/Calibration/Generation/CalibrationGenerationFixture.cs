using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;

namespace Farm.Web.Api.Tests.Calibration.Generation;

/// <summary>The seeded calibration aggregate a generation test operates on.</summary>
/// <param name="ProjectId">Owning project identity.</param>
/// <param name="AttemptId">Immutable attempt identity.</param>
/// <param name="OrchestrationId">Durable orchestration identity created with the attempt.</param>
/// <param name="PrinterId">Printer identity.</param>
/// <param name="SnapshotId">Immutable printer configuration snapshot identity.</param>
/// <param name="Method">Canonical calibration method name.</param>
/// <param name="Options">Typed method options stored on the attempt.</param>
/// <param name="Specification">The canonical specification stored on the attempt.</param>
/// <param name="Owner">The owning caller.</param>
internal sealed record CalibrationGenerationFixture(
    Guid ProjectId,
    Guid AttemptId,
    Guid OrchestrationId,
    Guid PrinterId,
    Guid SnapshotId,
    string Method,
    CalibrationMethodOptionsRequest Options,
    CalibrationSpecification Specification,
    CalibrationActor Owner)
{
    /// <summary>Builds the generation request that matches the stored attempt.</summary>
    /// <param name="baseRevision">Optional orchestration revision precondition.</param>
    /// <returns>The typed request.</returns>
    public CalibrationGenerateJobRequest Request(long? baseRevision = null) => new()
    {
        Method = Method,
        DefinitionVersion = CalibrationMethodOptions.CurrentDefinitionVersion,
        Options = Options,
        BaseRevision = baseRevision,
    };
}
