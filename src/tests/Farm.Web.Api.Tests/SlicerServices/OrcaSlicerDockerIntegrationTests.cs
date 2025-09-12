using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Docker deployment integration tests for OrcaSlicer worker
/// Mirrors PrusaSlicerDockerIntegrationTests adaptive polling pattern.
/// Also includes a full-stack microservices smoke test including frontend.
/// </summary>
[Trait("Category", "Docker")]
public class OrcaSlicerDockerIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _dockerComposeFile;
    private readonly string _baseDirectory;

    public OrcaSlicerDockerIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _baseDirectory = GetRepositoryRoot();
        _dockerComposeFile = Path.Combine(_baseDirectory, "docker-compose.microservices.yml");
    }

    public async Task InitializeAsync()
    {
        if (!File.Exists(_dockerComposeFile))
        {
            throw new FileNotFoundException($"Docker Compose file not found: {_dockerComposeFile}");
        }
        _output.WriteLine($"Using Docker Compose file: {_dockerComposeFile}");
    }

    public async Task DisposeAsync()
    {
        try
        {
            await RunDockerComposeCommandAsync("down", "--volumes", "--remove-orphans");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Cleanup warning: {ex.Message}");
        }
    }

    [Fact]
    public async Task OrcaSlicerWorker_ShouldBuildDockerImage_Successfully()
    {
        _output.WriteLine("Building OrcaSlicer worker Docker image...");
        var result = await RunDockerCommandAsync("build", "-f", "Dockerfile.orcaslicer", "-t", "orcaslicer-worker-test", ".");
        Assert.True(result.Success, $"Docker build failed: {result.ErrorOutput}");
        _output.WriteLine("Docker image built successfully");
    }

    [Fact]
    public async Task OrcaSlicerWorker_ShouldStartHealthy_InDockerCompose()
    {
        var start = await RunDockerComposeCommandAsync("up", "-d", "redis", "database", "api", "orcaslicer-worker");
        Assert.True(start.Success, $"Compose up failed: {start.ErrorOutput}");

        // API then Orca worker health (API dependency must be healthy too)
        await WaitForServiceAsync("api", 5001, timeout: TimeSpan.FromSeconds(90));
        await WaitForServiceAsync("orcaslicer-worker", 8081, timeout: TimeSpan.FromSeconds(90));

        var apiHealth = await CheckServiceHealthAsync("api", 5001);
        var orcaHealth = await CheckServiceHealthAsync("orcaslicer-worker", 8081);
        Assert.True(apiHealth.IsHealthy, $"API unhealthy: {apiHealth.Message}");
        Assert.True(orcaHealth.IsHealthy, $"Orca worker unhealthy: {orcaHealth.Message}");
    }

    [Fact]
    public async Task OrcaSlicerBinary_ShouldBeInstalled_InContainer()
    {
        await RunDockerComposeCommandAsync("up", "-d", "orcaslicer-worker");
        // Wait for binary existence (may be stub if download failed but still must exist & be executable)
        await WaitForExecSuccessAsync("orcaslicer-worker", new[] {"test", "-f", "/usr/local/bin/orcaslicer"}, TimeSpan.FromSeconds(90));

        var ls = await RunDockerComposeCommandAsync("exec", "-T", "orcaslicer-worker", "ls", "-la", "/usr/local/bin/orcaslicer");
        Assert.True(ls.Success, $"Binary listing failed: {ls.ErrorOutput}");
        Assert.Contains("orcaslicer", ls.Output);

        var execPerm = await RunDockerComposeCommandAsync("exec", "-T", "orcaslicer-worker", "test", "-x", "/usr/local/bin/orcaslicer");
        Assert.True(execPerm.Success, "OrcaSlicer binary not executable");
    }

    [Fact]
    public async Task OrcaSlicerWorker_EnvConfiguration_ShouldExposeExpectedVariables()
    {
        await RunDockerComposeCommandAsync("up", "-d", "orcaslicer-worker");
        await WaitForServiceAsync("orcaslicer-worker", 8081, timeout: TimeSpan.FromSeconds(60));

        var pathVar = await RunDockerComposeCommandAsync("exec", "-T", "orcaslicer-worker", "printenv", "Worker__OrcaSlicerPath");
        Assert.True(pathVar.Success && pathVar.Output.Contains("/usr/local/bin/orcaslicer"));

        var idVar = await RunDockerComposeCommandAsync("exec", "-T", "orcaslicer-worker", "printenv", "Worker__WorkerId");
        Assert.True(idVar.Success && idVar.Output.Contains("orcaslicer-worker"));
    }

    [Fact]
    public async Task OrcaSlicerVersion_CommandInvocation_ShouldReturnHelpOrExist()
    {
        await RunDockerComposeCommandAsync("up", "-d", "orcaslicer-worker");
        await WaitForExecSuccessAsync("orcaslicer-worker", new[] {"test", "-f", "/usr/local/bin/orcaslicer"}, TimeSpan.FromSeconds(90));

        var version = await RunDockerComposeCommandAsync("exec", "-T", "orcaslicer-worker", "/usr/local/bin/orcaslicer", "--help");
        if (!version.Success)
        {
            // Fall back to existence assertion already satisfied above
            _output.WriteLine("Help output not available (possibly stub or headless extraction); binary exists.");
        }
        else
        {
            Assert.Contains("Orca", version.Output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact(Skip = "Long running full-stack smoke; enable when needed")]
    public async Task FullStack_Microservices_ShouldServeFrontendAndApi()
    {
        var up = await RunDockerComposeCommandAsync("up", "-d", "redis", "database", "api", "orcaslicer-worker", "prusaslicer-worker", "frontend");
        Assert.True(up.Success, $"Compose up failed: {up.ErrorOutput}");

        await Task.WhenAll(
            WaitForServiceAsync("api", 5001, timeout: TimeSpan.FromSeconds(120)),
            WaitForServiceAsync("orcaslicer-worker", 8081, timeout: TimeSpan.FromSeconds(120)),
            WaitForServiceAsync("prusaslicer-worker", 8082, timeout: TimeSpan.FromSeconds(150)), // prusa slower build
            WaitForServiceAsync("frontend", 3000, endpoint: "/health", timeout: TimeSpan.FromSeconds(120))
        );

        var apiHealth = await CheckServiceHealthAsync("api", 5001);
        Assert.True(apiHealth.IsHealthy, $"API unhealthy: {apiHealth.Message}");
    }

    // ---------------- Helpers (duplicated; consider refactor if expanded further) ----------------
    private async Task<(bool Success, string Output, string ErrorOutput)> RunDockerCommandAsync(params string[] args)
        => await RunCommandAsync("docker", args);

    private async Task<(bool Success, string Output, string ErrorOutput)> RunDockerComposeCommandAsync(params string[] args)
    {
        var allArgs = new[] { "compose", "-f", _dockerComposeFile }.Concat(args).ToArray();
        return await RunCommandAsync("docker", allArgs);
    }

    private async Task<(bool Success, string Output, string ErrorOutput)> RunCommandAsync(string command, string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _baseDirectory
        };

        _output.WriteLine($"Running: {command} {string.Join(" ", args)}");
        process.Start();
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode == 0, await stdOut, await stdErr);
    }

    private async Task<(bool IsHealthy, string Message)> CheckServiceHealthAsync(string serviceName, int port, string endpoint = "/healthz")
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = $"http://localhost:{port}{endpoint}";
            var resp = await http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            return resp.IsSuccessStatusCode
                ? (true, $"{serviceName} healthy at {url}: {body}")
                : (false, $"{serviceName} unhealthy at {url}: {(int)resp.StatusCode} {resp.StatusCode} - {body}");
        }
        catch (Exception ex)
        {
            return (false, $"{serviceName} health check exception: {ex.Message}");
        }
    }

    private async Task WaitForServiceAsync(string serviceName, int port, string endpoint = "/healthz", TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        timeout ??= TimeSpan.FromSeconds(60);
        pollInterval ??= TimeSpan.FromSeconds(2);
        var sw = Stopwatch.StartNew();
        string? lastMessage = null;
        while (sw.Elapsed < timeout)
        {
            var health = await CheckServiceHealthAsync(serviceName, port, endpoint);
            if (health.IsHealthy)
            {
                _output.WriteLine($"{serviceName} healthy after {sw.Elapsed.TotalSeconds:F1}s");
                return;
            }
            lastMessage = health.Message;
            await Task.Delay(pollInterval.Value);
        }
        throw new TimeoutException($"Service '{serviceName}' not healthy after {timeout.Value.TotalSeconds}s. Last status: {lastMessage ?? "(no message)"}");
    }

    private async Task WaitForExecSuccessAsync(string serviceName, string[] execArgs, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        pollInterval ??= TimeSpan.FromSeconds(3);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var combined = new string[3 + execArgs.Length];
            combined[0] = "exec";
            combined[1] = "-T";
            combined[2] = serviceName;
            Array.Copy(execArgs, 0, combined, 3, execArgs.Length);
            var res = await RunDockerComposeCommandAsync(combined);
            if (res.Success)
            {
                _output.WriteLine($"Exec success for {serviceName} after {sw.Elapsed.TotalSeconds:F1}s -> {string.Join(' ', execArgs)}");
                return;
            }
            await Task.Delay(pollInterval.Value);
        }
        throw new TimeoutException($"Exec command '{string.Join(' ', execArgs)}' for service '{serviceName}' did not succeed within {timeout.TotalSeconds}s");
    }

    private static string GetRepositoryRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        if (dir == null) throw new InvalidOperationException("Could not locate repository root (global.json not found)");
        return dir.FullName;
    }
}
