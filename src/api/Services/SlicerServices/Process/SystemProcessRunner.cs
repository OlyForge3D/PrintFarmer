using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Web.Api.Services.SlicerServices.Process;

public class SystemProcessRunner : IProcessRunner
{
    public IProcessHandle Start(System.Diagnostics.ProcessStartInfo startInfo)
    {
        var proc = System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start process");
        return new SystemProcessHandle(proc);
    }

    private sealed class SystemProcessHandle : IProcessHandle
    {
        private readonly System.Diagnostics.Process _proc;

        public SystemProcessHandle(System.Diagnostics.Process proc)
        {
            _proc = proc;
        }

        public System.IO.StreamReader StandardOutput => _proc.StandardOutput;
        public System.IO.StreamReader StandardError => _proc.StandardError;
        public bool HasExited => _proc.HasExited;
        public int ExitCode => _proc.ExitCode;

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _proc.WaitForExitAsync(cancellationToken);
                return _proc.ExitCode;
            }
            catch (OperationCanceledException)
            {
                // If cancelled, try to kill process gracefully and return non-zero
                try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }
        }

        public void Kill()
        {
            try
            {
                if (!_proc.HasExited)
                {
                    _proc.Kill(entireProcessTree: true);
                }
            }
            catch { /* best effort */ }
        }
    }
}
