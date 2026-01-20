using Farm.Infrastructure;

namespace Farm.Slicer.Worker.Core;

public static class WorkerIdentity
{
    public static string Create() => Environment.MachineName + "-" + Environment.ProcessId;
}
