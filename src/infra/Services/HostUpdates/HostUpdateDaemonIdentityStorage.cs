using System.Security;

namespace Farm.Infrastructure.Services.HostUpdates;

public enum HostUpdateDaemonIdentityStorageState
{
    /// <summary>No identity directory is configured; the daemon runs unenrolled.</summary>
    NotConfigured,

    /// <summary>The configured directory or key file does not exist yet; the daemon runs unenrolled.</summary>
    Absent,

    /// <summary>Storage passed every ownership, permission and reparse check.</summary>
    Valid,

    /// <summary>Storage failed a check; the daemon refuses to run as enrolled.</summary>
    Invalid,
}

/// <summary>Outcome of inspecting host identity storage. Carries a fixed code only, never paths or key material.</summary>
public sealed record HostUpdateDaemonIdentityStorageStatus(HostUpdateDaemonIdentityStorageState State, string Code)
{
    public bool UsableForEnrollment => State == HostUpdateDaemonIdentityStorageState.Valid;
}

/// <summary>
/// Read-only validation of the daemon's host-held identity storage (issue #3114, design
/// docs/HOST_UPDATE_DAEMON_SECURITY.md#storage). The private key must be a regular <c>0600</c>
/// file in a <c>0700</c> directory, both owned by the daemon's effective user, with no
/// symlink/reparse component, outside the executor root (so executor backups never copy it).
/// The key's bytes are never read here: this only proves the storage is safe to use.
/// Only Linux is qualified; Windows CNG storage is future work (#3118) and fails closed.
/// </summary>
public static class HostUpdateDaemonIdentityStorage
{
    public const string KeyFileName = "enrollment-key.pem";

    private const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public static HostUpdateDaemonIdentityStorageStatus Inspect(string? identityDirectory, string? executorRootDirectory) =>
        Inspect(identityDirectory, executorRootDirectory, OperatingSystem.IsLinux(), LinuxOwner);

    internal static HostUpdateDaemonIdentityStorageStatus Inspect(
        string? identityDirectory,
        string? executorRootDirectory,
        bool isLinux,
        Func<string, bool> ownedByEffectiveUser)
    {
        if (string.IsNullOrWhiteSpace(identityDirectory))
        {
            return new(HostUpdateDaemonIdentityStorageState.NotConfigured, "identity_not_configured");
        }

        if (!Path.IsPathFullyQualified(identityDirectory))
        {
            return Invalid("identity_path_not_absolute");
        }

        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(identityDirectory));
        if (!string.IsNullOrWhiteSpace(executorRootDirectory) && IsSameOrUnder(directory, executorRootDirectory))
        {
            return Invalid("identity_inside_executor_root");
        }

        if (!isLinux)
        {
            return Invalid("identity_storage_platform_unsupported");
        }

        try
        {
            HostStateFileSecurity.ValidateExistingPathComponents(directory);
            if (!Directory.Exists(directory))
            {
                return new(HostUpdateDaemonIdentityStorageState.Absent, "identity_absent");
            }

            if (HostStateFileSecurity.IsReparsePoint(directory))
            {
                return Invalid("identity_reparse_rejected");
            }

            if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(directory) & GroupOrOther) != 0)
            {
                return Invalid("identity_directory_permissions_insecure");
            }

            if (!ownedByEffectiveUser(directory))
            {
                return Invalid("identity_directory_owner_mismatch");
            }

            string key = Path.Combine(directory, KeyFileName);
            if (Directory.Exists(key))
            {
                return Invalid("identity_key_not_regular_file");
            }

            if (!File.Exists(key))
            {
                return new(HostUpdateDaemonIdentityStorageState.Absent, "identity_key_absent");
            }

            if (HostStateFileSecurity.IsReparsePoint(key))
            {
                return Invalid("identity_reparse_rejected");
            }

            if (OperatingSystem.IsLinux() &&
                (File.GetUnixFileMode(key) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
            {
                return Invalid("identity_key_permissions_insecure");
            }

            if (!ownedByEffectiveUser(key))
            {
                return Invalid("identity_key_owner_mismatch");
            }

            using (new FileStream(key, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Opening proves readability; the key bytes are intentionally not read.
            }

            return new(HostUpdateDaemonIdentityStorageState.Valid, "identity_storage_valid");
        }
        catch (SecurityException)
        {
            return Invalid("identity_reparse_rejected");
        }
        catch (UnauthorizedAccessException)
        {
            return Invalid("identity_unreadable");
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or EntryPointNotFoundException or DllNotFoundException)
        {
            return Invalid("identity_owner_validation_unavailable");
        }
    }

    private static HostUpdateDaemonIdentityStorageStatus Invalid(string code) => new(HostUpdateDaemonIdentityStorageState.Invalid, code);

    private static bool IsSameOrUnder(string candidate, string root)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(candidate, fullRoot, comparison) ||
            candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static bool LinuxOwner(string path) =>
        HostStateFileSecurity.NativeMethods.GetLinuxOwnerUserId(path) == HostStateFileSecurity.NativeMethods.GetEffectiveUserId();
}
