using GrpcJobPriority = Farm.Web.Api.Grpc.JobPriority;
using GrpcJobStatus = Farm.Web.Api.Grpc.JobStatus;
using GrpcSlicerEngineType = Farm.Web.Api.Grpc.SlicerEngineType;
using GrpcWorkerStatus = Farm.Web.Api.Grpc.WorkerStatus;

namespace Farm.Slicer.Module.Tests.ContractTests;

/// <summary>
/// Tests to validate the pre-generated gRPC protocol buffer stubs compile correctly.
/// These tests ensure the proto/slicer_jobs.proto enums are available in C#.
/// </summary>
public class SlicerJobsProtoCompilationTests
{
    [Fact]
    public void JobStatusEnum_ContainsExpectedValues()
    {
        Assert.True(Enum.IsDefined(GrpcJobStatus.Unknown));
        Assert.True(Enum.IsDefined(GrpcJobStatus.Queued));
        Assert.True(Enum.IsDefined(GrpcJobStatus.Slicing));
        Assert.True(Enum.IsDefined(GrpcJobStatus.Completed));
        Assert.True(Enum.IsDefined(GrpcJobStatus.Error));
        Assert.True(Enum.IsDefined(GrpcJobStatus.Cancelled));
    }

    [Fact]
    public void JobPriorityEnum_ContainsExpectedValues()
    {
        Assert.True(Enum.IsDefined(GrpcJobPriority.Unknown));
        Assert.True(Enum.IsDefined(GrpcJobPriority.Low));
        Assert.True(Enum.IsDefined(GrpcJobPriority.Normal));
        Assert.True(Enum.IsDefined(GrpcJobPriority.High));
        Assert.True(Enum.IsDefined(GrpcJobPriority.Critical));
    }

    [Fact]
    public void SlicerEngineTypeEnum_ContainsExpectedValues()
    {
        Assert.True(Enum.IsDefined(GrpcSlicerEngineType.Unknown));
        Assert.True(Enum.IsDefined(GrpcSlicerEngineType.Orca));
        Assert.True(Enum.IsDefined(GrpcSlicerEngineType.Prusa));
        Assert.True(Enum.IsDefined(GrpcSlicerEngineType.Super));
        Assert.True(Enum.IsDefined(GrpcSlicerEngineType.Cura));
    }

    [Fact]
    public void WorkerStatusEnum_ContainsExpectedValues()
    {
        Assert.True(Enum.IsDefined(GrpcWorkerStatus.Unknown));
        Assert.True(Enum.IsDefined(GrpcWorkerStatus.Idle));
        Assert.True(Enum.IsDefined(GrpcWorkerStatus.Busy));
        Assert.True(Enum.IsDefined(GrpcWorkerStatus.Offline));
        Assert.True(Enum.IsDefined(GrpcWorkerStatus.Maintenance));
        Assert.True(Enum.IsDefined(GrpcWorkerStatus.Error));
    }
}
