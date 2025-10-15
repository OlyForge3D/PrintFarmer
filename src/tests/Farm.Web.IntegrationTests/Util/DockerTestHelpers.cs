using System.Diagnostics;
using Xunit.Abstractions;

namespace Farm.Web.IntegrationTests.Util;

/// <summary>
/// Shared helper utilities for Docker-based integration tests providing adaptive polling.
/// Copied into the integration project so it is self-contained.
/// </summary>
public static class DockerTestHelpers
{
    public static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }
        if (directory == null)
        {
            throw new InvalidOperationException("Could not find repository root (global.json not found)");
        }

        return directory.FullName;
    }

    public static Task<(bool Success, string Output, string ErrorOutput)> RunDockerCommandAsync(ITestOutputHelper output, string workingDir, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(workingDir);
        ArgumentNullException.ThrowIfNull(args);
        return RunCommandAsync(output, workingDir, "docker", args);
    }

    public static Task<(bool Success, string Output, string ErrorOutput)> RunDockerComposeCommandAsync(ITestOutputHelper output, string composeFile, string workingDir, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(composeFile);
        ArgumentNullException.ThrowIfNull(workingDir);
        ArgumentNullException.ThrowIfNull(args);
        var allArgs = new[] { "compose", "-f", composeFile }.Concat(args).ToArray();
        return RunCommandAsync(output, workingDir, "docker", allArgs);
    }

    private static async Task<(bool Success, string Output, string ErrorOutput)> RunCommandAsync(ITestOutputHelper output, string workingDir, string command, string[] args)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(workingDir);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(args);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDir
        };
        // Allow short-circuit for quick test runs that should not execute Docker.
        string? skip = Environment.GetEnvironmentVariable("SKIP_DOCKER_TESTS");
        if (!string.IsNullOrEmpty(skip) && (skip == "1" || skip.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            output.WriteLine($"SKIP_DOCKER_TESTS set - skipping execution of: {command} {string.Join(' ', args)}");
            return (true, "(skipped)", string.Empty);
        }

        output.WriteLine($"Running: {command} {string.Join(" ", args)}");
        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;
        return (process.ExitCode == 0, stdOut, stdErr);
    }

    public static async Task<(bool IsHealthy, string Message)> CheckServiceHealthAsync(string serviceName, int port, string endpoint = "/healthz")
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

    public static async Task WaitForServiceAsync(ITestOutputHelper output, string serviceName, int port, string endpoint = "/healthz", TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(serviceName);
        timeout ??= TimeSpan.FromSeconds(60);
        pollInterval ??= TimeSpan.FromSeconds(2);
        var sw = Stopwatch.StartNew();
        string? lastMessage = null;
        while (sw.Elapsed < timeout)
        {
            var health = await CheckServiceHealthAsync(serviceName, port, endpoint);
            if (health.IsHealthy)
            {
                output.WriteLine($"{serviceName} healthy after {sw.Elapsed.TotalSeconds:F1}s");
                return;
            }
            lastMessage = health.Message;
            await Task.Delay(pollInterval.Value);
        }
        throw new TimeoutException($"Service '{serviceName}' not healthy after {timeout.Value.TotalSeconds}s. Last status: {lastMessage ?? "(no message)"}");
    }

    public static async Task WaitForExecSuccessAsync(ITestOutputHelper output, string composeFile, string workingDir, string serviceName, string[] execArgs, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(composeFile);
        ArgumentNullException.ThrowIfNull(workingDir);
        ArgumentNullException.ThrowIfNull(serviceName);
        ArgumentNullException.ThrowIfNull(execArgs);
        pollInterval ??= TimeSpan.FromSeconds(3);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var combined = new string[3 + execArgs.Length];
            combined[0] = "exec";
            combined[1] = "-T";
            combined[2] = serviceName;
            Array.Copy(execArgs, 0, combined, 3, execArgs.Length);
            var res = await RunDockerComposeCommandAsync(output, composeFile, workingDir, combined);
            if (res.Success)
            {
                output.WriteLine($"Exec success for {serviceName} after {sw.Elapsed.TotalSeconds:F1}s -> {string.Join(' ', execArgs)}");
                return;
            }
            await Task.Delay(pollInterval.Value);
        }
        throw new TimeoutException($"Exec command '{string.Join(' ', execArgs)}' for service '{serviceName}' did not succeed within {timeout.TotalSeconds}s");
    }
}
