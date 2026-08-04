namespace Farm.Slicer.Worker.Core;

public static class WorkerIdentity
{
    public static string Create() => Guid.NewGuid().ToString("N");
}
