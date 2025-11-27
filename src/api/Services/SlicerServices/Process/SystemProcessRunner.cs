

namespace Farm.Web.Api.Services.SlicerServices.Process;

public class SystemProcessRunner : IProcessRunner
{
    public IProcessHandle Start(System.Diagnostics.ProcessStartInfo startInfo)
    {
        System.Diagnostics.Process proc = System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start process");
        return new SystemProcessHandle(proc);
    }

    private sealed class SystemProcessHandle(System.Diagnostics.Process proc) : IProcessHandle
    {
        private readonly System.Diagnostics.Process _proc = proc;

        public StreamReader StandardOutput => _proc.StandardOutput;
        public StreamReader StandardError => _proc.StandardError;
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
                try
                {
                    if (!_proc.HasExited)
                    {
                        _proc.Kill(entireProcessTree: true);
                    }
                }
                catch { }
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
