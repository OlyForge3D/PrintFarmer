using System.Reflection;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Infrastructure.Tests.Services.Printers;

public sealed class PrinterControlOwnershipTests
{
    [Theory]
    [InlineData(typeof(PrintersService))]
    [InlineData(typeof(Farm.Infrastructure.Services.Queue.PrinterPhysicalActuationService))]
    [InlineData(typeof(PrinterBackendCapabilitiesService))]
    public void SharedControlService_ConstructorDependencies_AreBackendNeutral(Type serviceType)
    {
        IEnumerable<Type> dependencies = serviceType.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType);

        dependencies.Should().NotContain(type =>
            type.Name.Contains("Moonraker", StringComparison.Ordinal) ||
            type.Name.Contains("OctoPrint", StringComparison.Ordinal) ||
            type.Name.Contains("PrusaLink", StringComparison.Ordinal));
    }

    [Fact]
    public void SharedAssembly_DoesNotOwnEndpointResolverOrFirmwareScriptBuilder()
    {
        Assembly assembly = typeof(PrintersService).Assembly;

        assembly.GetType("Farm.Infrastructure.Services.Printers.PrinterBackendEndpointResolver")
            .Should().BeNull();
        assembly.GetType("Farm.Infrastructure.Services.Printers.IMoonrakerMotionChannelFactory")
            .Should().BeNull();
        assembly.GetType("Farm.Infrastructure.Services.Printers.PrinterControlIntent")
            .Should().BeNull();
    }

    [Fact]
    public void SharedAssembly_RemovedTrackingRuntime_IsAbsent()
    {
        Assembly assembly = typeof(PrintersService).Assembly;
        assembly.GetType("Farm.Infrastructure.Services.Printers.PrinterControlOperationService")
            .Should().BeNull();
        assembly.GetType("Farm.Infrastructure.Services.Printers.PrinterControlOperationWorker")
            .Should().BeNull();
        assembly.GetType("Farm.Infrastructure.Services.Printers.IPrinterMotionChannel")
            .Should().BeNull();
        assembly.GetType("Farm.Infrastructure.ISupportsDurableMotion")
            .Should().BeNull();
    }
}
