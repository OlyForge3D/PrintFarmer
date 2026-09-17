#pragma warning disable SA1501, SA1503, SA1515, SA1408, SA1518, SA1513, CA5392, CA2101
using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.HostUpdates;

public sealed class HostStateOptions
{
    public const string SectionName = "HostUpdates:HostState";

    public bool Enabled { get; set; }

    public bool ProvisioningEnabled { get; set; }

    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// Deployment attestation that the Windows directory has an administrator-provisioned,
    /// service-identity-only ACL. Required because portable .NET APIs cannot reliably prove ACL
    /// ownership across local, domain, and container identities.
    /// </summary>
    public bool WindowsSecurityAttested { get; set; }
}

public sealed class HostStateOptionsValidator : IValidateOptions<HostStateOptions>
{
    public ValidateOptionsResult Validate(string? name, HostStateOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        try
        {
            _ = HostStateFileSecurity.PrepareAndValidateRoot(options);
            return ValidateOptionsResult.Success;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            return ValidateOptionsResult.Fail($"Host state root security validation failed: {ex.Message}");
        }
    }
}

public sealed class HostStatePath
{
    public HostStatePath(IOptions<HostStateOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Root = HostStateFileSecurity.PrepareAndValidateRoot(options.Value);
    }

    public string Root { get; }

    public string Resolve(string relativeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeName);
        HostStateFileSecurity.ValidateExistingPathComponents(Root);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
        string path = Path.GetFullPath(Path.Combine(root, relativeName));
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("host_state_path_escape");
        }

        HostStateFileSecurity.ValidateExistingPathComponents(path);
        return path;
    }
}

public static class HostStateFileSecurity
{
    private const UnixFileMode UnsafeUnixWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    public static string PrepareAndValidateRoot(HostStateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RootPath) || !Path.IsPathFullyQualified(options.RootPath))
        {
            throw new ArgumentException("HostUpdates:HostState:RootPath must be an absolute persistent path.");
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
        ValidateExistingPathComponents(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("host_state_root_missing");
        }

        ValidateExistingPathComponents(root);
        if (OperatingSystem.IsWindows())
        {
            if (!options.WindowsSecurityAttested)
            {
                throw new SecurityException("host_state_windows_acl_attestation_required");
            }
        }
        else
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new SecurityException("host_state_unix_owner_validation_unavailable");
            }
            UnixFileMode mode = File.GetUnixFileMode(root);
            if ((mode & UnsafeUnixWrite) != 0)
            {
                throw new SecurityException("host_state_unix_permissions_insecure");
            }

            try
            {
                NativeMethods.LinuxStat stat = default;
                if (OperatingSystem.IsLinux() && NativeMethods.Stat(root, out stat) != 0)
                {
                    throw new IOException("host_state_owner_stat_failed");
                }
                else if (OperatingSystem.IsLinux() && stat.UserId != NativeMethods.GetEffectiveUserId())
                {
                    throw new SecurityException("host_state_owner_mismatch");
                }
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or PlatformNotSupportedException)
            {
                throw new SecurityException("host_state_owner_validation_unavailable", ex);
            }
        }

        string probe = Path.Combine(root, ".host-state-write-test-" + Guid.NewGuid().ToString("N"));
        RejectReparseTarget(probe);
        try
        {
            using FileStream stream = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
            stream.Flush(true);
        }
        finally
        {
            if (File.Exists(probe) && !IsReparsePoint(probe))
            {
                File.Delete(probe);
            }
        }

        return root;
    }

    public static void ValidateExistingPathComponents(string path)
    {
        string full = Path.GetFullPath(path);
        string? current = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(current))
        {
            throw new InvalidOperationException("host_state_path_invalid");
        }

        foreach (string component in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((Directory.Exists(current) || File.Exists(current)) && IsReparsePoint(current))
            {
                throw new SecurityException("host_state_reparse_path_rejected");
            }
        }
    }

    public static bool IsReparsePoint(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return false;
        }

        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    public static void RejectReparseTarget(string path)
    {
        ValidateExistingPathComponents(path);
        if (IsReparsePoint(path))
        {
            throw new SecurityException("host_state_reparse_target_rejected");
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct LinuxStat
        {
            internal ulong Device;
            internal ulong Inode;
            internal ulong HardLinks;
            internal uint Mode;
            internal uint UserId;
            internal uint GroupId;
            internal int Padding;
            internal ulong RDevice;
            internal long Size;
            internal long BlockSize;
            internal long Blocks;
            internal long AccessTime;
            internal long AccessTimeNanoseconds;
            internal long ModificationTime;
            internal long ModificationTimeNanoseconds;
            internal long ChangeTime;
            internal long ChangeTimeNanoseconds;
            internal long Reserved1;
            internal long Reserved2;
            internal long Reserved3;
        }

        [DllImport("libc", EntryPoint = "geteuid")]
        internal static extern uint GetEffectiveUserId();

        [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
        internal static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out LinuxStat stat);
    }
}
