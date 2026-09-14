using System.Reflection;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Infrastructure.Tests.Services.Printers;

public sealed class PrinterControlOwnershipTests
{
    [Theory]
    [InlineData(typeof(PrinterControlOperationService))]
    [InlineData(typeof(PrinterControlOperationWorker))]
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
        Type? intent = assembly.GetType("Farm.Infrastructure.Services.Printers.PrinterControlIntent");
        intent.Should().NotBeNull();
        intent!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Should().NotContain(method => method.Name == "BuildScript");
    }

    [Fact]
    public void DurableMotionChannel_ExecuteContract_AcceptsSemanticIntentNotFirmwareScript()
    {
        MethodInfo? execute = typeof(IPrinterMotionChannel).GetMethod(nameof(IPrinterMotionChannel.ExecuteAsync));
        execute.Should().NotBeNull();
        execute!.GetParameters().Select(parameter => parameter.ParameterType)
            .Should().Equal(typeof(Guid), typeof(PrinterControlRequest), typeof(CancellationToken));
    }
}
