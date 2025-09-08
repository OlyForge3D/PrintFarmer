namespace Farm.OrcaSlicer.Worker.Services;

public interface IOrcaBinaryDetector
{
    bool IsRealBinaryPresent();
    string? BinaryPath { get; }
}

public class OrcaBinaryDetector : IOrcaBinaryDetector
{
    private readonly string _path;
    private bool? _cached;
    public OrcaBinaryDetector()
    {
        _path = Environment.GetEnvironmentVariable("Worker__OrcaSlicerPath") ?? "/usr/local/bin/orcaslicer";
    }

    public string? BinaryPath => _path;

    public bool IsRealBinaryPresent()
    {
        if (_cached.HasValue)
            return _cached.Value;
        try
        {
            if (!File.Exists(_path))
            {
                return Cache(false);
            }
            var fi = new FileInfo(_path);
            if (fi.Length <= 2048)
            {
                return Cache(false);
            }
            // Optional lightweight exec to ensure not stub renamed
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _path,
                Arguments = "--help",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null)
            {
                return Cache(false);
            }
            if (!p.WaitForExit(4000))
            {
                try
                {
                    p.Kill();
                }
                catch
                {
                    // ignore
                }
                return Cache(false);
            }
            return Cache(p.ExitCode == 0 || p.ExitCode == 1);
        }
        catch
        {
            return Cache(false);
        }
    }

    private bool Cache(bool value) { _cached = value; return value; }
}
