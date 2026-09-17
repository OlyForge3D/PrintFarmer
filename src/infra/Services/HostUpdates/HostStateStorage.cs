#pragma warning disable CA1512, CA1849, CA1859, CA1823, S1144, S3218, SA1107, SA1210, SA1501, SA1503, SA1513, SA1516, SA1518
using System.Security;
using System.ComponentModel.DataAnnotations;
using System.Security.AccessControl;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.HostUpdates;

public sealed class HostStateOptions
{
    public const string SectionName = "HostUpdates:HostState";
    public string RootPath { get; set; } = string.Empty;
}

public sealed class HostStateOptionsValidator : IValidateOptions<HostStateOptions>
{
    public ValidateOptionsResult Validate(string? name, HostStateOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath))
            return ValidateOptionsResult.Fail("HostUpdates:HostState:RootPath is required and must be an absolute persistent path.");
        if (!Path.IsPathFullyQualified(options.RootPath))
            return ValidateOptionsResult.Fail("HostUpdates:HostState:RootPath must be absolute.");
        try
        {
            string root = Path.GetFullPath(options.RootPath);
            DirectoryInfo info = Directory.CreateDirectory(root);
            string probe = Path.Combine(root, ".host-state-write-test-" + Guid.NewGuid().ToString("N"));
            using (FileStream stream = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Flush(true);
            File.Delete(probe);
            return ValidateOptionsResult.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return ValidateOptionsResult.Fail($"Host state root is unavailable or unwritable: {ex.Message}");
        }
    }
}

public sealed class HostStatePath
{
    public HostStatePath(IOptions<HostStateOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Root = Path.GetFullPath(options.Value.RootPath);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Resolve(string relativeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeName);
        string root = Path.GetFullPath(Root + Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(Root, relativeName));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("host_state_path_escape");
        return path;
    }
}


