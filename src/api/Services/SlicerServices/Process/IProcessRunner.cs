using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Web.Api.Services.SlicerServices.Process;

public interface IProcessHandle
{
    System.IO.StreamReader StandardOutput { get; }
    System.IO.StreamReader StandardError { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
    /// <summary>
    /// Attempt to terminate the underlying process.
    /// </summary>
    void Kill();
}

public interface IProcessRunner
{
    /// Start a process with the given ProcessStartInfo and return a handle representing it.
    IProcessHandle Start(ProcessStartInfo startInfo);
}
