#pragma warning disable SA1501, SA1503, SA1515, SA1408, SA1518, SA1513, CA5392, CA2101
using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
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

    private HostStatePath(string root) => Root = root;

    public string Root { get; }

    /// <summary>Opens the root with full security validation but without the write probe.</summary>
    public static HostStatePath OpenReadOnly(HostStateOptions options) =>
        new(HostStateFileSecurity.PrepareAndValidateRoot(options, verifyWritable: false));

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

public static class HostUpdateInstallationIdentity
{
    private const string FileName = "installation.id";
    private static readonly object Gate = new();

    public static string GetOrCreate(string? hostStateRoot)
    {
        if (string.IsNullOrWhiteSpace(hostStateRoot))
        {
            // Unprovisioned hosts must not write outside the validated host-state root.
            return CreateIdentity();
        }

        string root = Path.GetFullPath(hostStateRoot);
        HostStateFileSecurity.ValidateExistingPathComponents(root);
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CreateIdentity();
        }
        HostStateFileSecurity.ValidateExistingPathComponents(root);
        string path = Path.Combine(root, FileName);

        lock (Gate)
        {
            try
            {
                HostStateFileSecurity.RejectReparseTarget(path);
                if (File.Exists(path))
                {
                    HostStateFileSecurity.RejectReparseTarget(path);
                    string existing = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(existing))
                    {
                        return existing;
                    }
                }

                string identity = CreateIdentity();
                HostStateFileSecurity.RejectReparseTarget(path);
                File.WriteAllText(path, identity);
                return identity;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return CreateIdentity();
            }
        }
    }

    private static string CreateIdentity() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

public static class HostStateFileSecurity
{
    private const UnixFileMode UnsafeUnixWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    public static string PrepareAndValidateRoot(HostStateOptions options) => PrepareAndValidateRoot(options, verifyWritable: true);

    /// <summary>
    /// Validates the host-state root. <paramref name="verifyWritable"/> <c>false</c> performs every
    /// ownership, permission and reparse check but skips the create/delete write probe, for
    /// read-only consumers (issue #2998) that must not touch the directory at all.
    /// </summary>
    public static string PrepareAndValidateRoot(HostStateOptions options, bool verifyWritable)
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
                uint owner = NativeMethods.GetLinuxOwnerUserId(root);
                if (owner != NativeMethods.GetEffectiveUserId())
                {
                    throw new SecurityException("host_state_owner_mismatch");
                }
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or PlatformNotSupportedException)
            {
                throw new SecurityException("host_state_owner_validation_unavailable", ex);
            }
        }

        if (!verifyWritable)
        {
            return root;
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

    internal static class NativeMethods
    {
        private const int AtFdcwd = -100;
        private const uint StatxBasicStats = 0x7ff;
        private const uint StatxUid = 0x0008;
        private const long SysStatxX64 = 332;
        private const long SysStatxArm64 = 291;

        [StructLayout(LayoutKind.Sequential)]
        internal struct LinuxStatx
        {
            internal uint Mask;
            internal uint BlockSize;
            internal ulong Attributes;
            internal uint HardLinks;
            internal uint UserId;
            internal uint GroupId;
            internal ushort Mode;
            internal ushort Padding0;
            internal ulong Inode;
            internal ulong Size;
            internal ulong Blocks;
            internal ulong AttributesMask;
            internal StatxTimestamp AccessTime;
            internal StatxTimestamp BirthTime;
            internal StatxTimestamp ChangeTime;
            internal StatxTimestamp ModificationTime;
            internal uint RdevMajor;
            internal uint RdevMinor;
            internal uint DevMajor;
            internal uint DevMinor;
            internal ulong MountId;
            internal uint DirectIoMemAlign;
            internal uint DirectIoOffsetAlign;
            internal ulong Spare0;
            internal ulong Spare1;
            internal ulong Spare2;
            internal ulong Spare3;
            internal ulong Spare4;
            internal ulong Spare5;
            internal ulong Spare6;
            internal ulong Spare7;
            internal ulong Spare8;
            internal ulong Spare9;
            internal ulong Spare10;
            internal ulong Spare11;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StatxTimestamp
        {
            internal long Seconds;
            internal uint Nanoseconds;
            internal int Reserved;
        }

        internal static uint GetLinuxOwnerUserId(string path)
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("host_state_owner_validation_unavailable");
            }

            long syscallNumber = StatxSyscallNumberForArchitecture(RuntimeInformation.ProcessArchitecture);

            LinuxStatx stat = default;
            long result = Syscall(syscallNumber, AtFdcwd, path, 0, StatxBasicStats, ref stat);
            if (result != 0 || (stat.Mask & StatxUid) == 0)
            {
                throw new IOException("host_state_owner_stat_failed");
            }

            return stat.UserId;
        }

        internal static long StatxSyscallNumberForArchitecture(Architecture architecture) => architecture switch
        {
            Architecture.X64 => SysStatxX64,
            Architecture.Arm64 => SysStatxArm64,
            _ => throw new PlatformNotSupportedException("host_state_owner_validation_unavailable"),
        };

        [DllImport("libc", EntryPoint = "geteuid")]
        internal static extern uint GetEffectiveUserId();

        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static extern long Syscall(long number, int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, ref LinuxStatx stat);
    }
}
