using System.Diagnostics;
using Farm.Web.Api.Services.SlicerServices.Process;

namespace Farm.Web.Api.Tests.TestUtilities;

public class TestProcessRunner : IProcessRunner
{
    private readonly Func<ProcessStartInfo, IProcessHandle> _factory;

    public TestProcessRunner(Func<ProcessStartInfo, IProcessHandle>? factory = null)
    {
        _factory = factory ?? (psi => new TestProcessHandle(new[] { "Progress: 50%", "Exported gcode" }));
    }

    public IProcessHandle Start(ProcessStartInfo startInfo)
    {
        return _factory(startInfo);
    }

    private class TestProcessHandle : IProcessHandle
    {
        private readonly System.IO.MemoryStream _ms;
        private readonly System.IO.StreamReader _sr;
        private bool _killed;

        public TestProcessHandle(IEnumerable<string> lines)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines) + '\n');
            _ms = new System.IO.MemoryStream(bytes);
            _sr = new System.IO.StreamReader(_ms);
            _killed = false;
        }

        public System.IO.StreamReader StandardOutput => _sr;
        public System.IO.StreamReader StandardError => new System.IO.StreamReader(new System.IO.MemoryStream());
        public bool HasExited => _killed || _ms.Position >= _ms.Length;
        public int ExitCode { get; private set; } = 0;

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            _killed = true; // simulate immediate exit on wait
            return Task.FromResult(ExitCode);
        }

        public void Kill()
        {
            _killed = true;
            ExitCode = -1;
        }
    }
}
