using System.Security;
using Farm.Infrastructure.Services.HostUpdates;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Test-only temp paths for host-state persistence. Production <see cref="HostStateFileSecurity"/>
/// rejects every reparse/symlink path component, and some hosts route the default temp directory
/// through a symlink (macOS: <c>/var</c> -> <c>/private/var</c>). Tests therefore resolve the temp
/// directory to its physical path once, instead of relaxing the production check.
/// See docs/TESTING_PATTERNS.md#host-state-persistence-platform-prerequisites.
/// </summary>
internal static class HostStateTestPaths
{
    private const int MaxLinkTraversals = 40;
    private static readonly Lazy<string> PhysicalTempRoot = new(ResolvePhysicalTempRoot);

    /// <summary>Physical (symlink-free) equivalent of <see cref="Path.GetTempPath"/>.</summary>
    public static string TempRoot => PhysicalTempRoot.Value;

    /// <summary>Physical-root equivalent of <see cref="Directory.CreateTempSubdirectory(string?)"/> (owner-only on Unix).</summary>
    public static DirectoryInfo CreateTempSubdirectory(string prefix)
    {
        string path = Path.Combine(TempRoot, prefix + Guid.NewGuid().ToString("N"));
        return OperatingSystem.IsWindows()
            ? Directory.CreateDirectory(path)
            : Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Resolves every existing reparse/symlink component of <paramref name="path"/> to its target.</summary>
    public static string ResolvePhysicalPath(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (int traversal = 0; traversal <= MaxLinkTraversals; traversal++)
        {
            string? link = FindFirstReparseComponent(full);
            if (link is null)
            {
                return full;
            }

            string target = new DirectoryInfo(link).LinkTarget ?? new FileInfo(link).LinkTarget
                ?? throw new IOException($"Reparse point '{link}' has no resolvable link target.");
            string resolvedLink = Path.GetFullPath(target, Path.GetDirectoryName(link)!);
            string remainder = Path.GetRelativePath(link, full);
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(remainder == "." ? resolvedLink : Path.Combine(resolvedLink, remainder)));
        }

        throw new IOException($"Too many symbolic links while resolving '{path}'.");
    }

    private static string? FindFirstReparseComponent(string full)
    {
        string current = Path.GetPathRoot(full) ?? throw new IOException($"'{full}' has no root.");
        foreach (string component in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (HostStateFileSecurity.IsReparsePoint(current))
            {
                return current;
            }
        }

        return null;
    }

    private static string ResolvePhysicalTempRoot()
    {
        string configured = Path.GetTempPath();
        try
        {
            string physical = ResolvePhysicalPath(configured);
            HostStateFileSecurity.ValidateExistingPathComponents(physical);
            return physical;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new InvalidOperationException(
                $"Host-state tests need a temp directory without symlink/reparse components, but '{configured}' could not be resolved to one. " +
                "Point TMPDIR (or TEMP on Windows) at a physical directory. See docs/TESTING_PATTERNS.md#host-state-persistence-platform-prerequisites.",
                ex);
        }
    }
}

/// <summary>
/// Production host-state root validation can only prove the root's ownership on Linux (statx
/// owner vs. effective uid) or via the Windows ACL attestation. Everywhere else (for example
/// macOS) it deliberately fails closed with <c>host_state_unix_owner_validation_unavailable</c>.
/// </summary>
internal static class HostStateOwnerValidation
{
    public const string UnavailableSkipReason =
        "Requires host-state owner validation (Linux statx or Windows ACL attestation). On this platform " +
        "HostStateFileSecurity fails closed with host_state_unix_owner_validation_unavailable, which " +
        "HostStatePersistenceTests.HostStateRoot_FailsClosedWhereUnixOwnerValidationIsUnavailable asserts. " +
        "See docs/TESTING_PATTERNS.md#host-state-persistence-platform-prerequisites.";

    public const string SupportedSkipReason =
        "Asserts the fail-closed outcome on platforms without host-state owner validation; this platform supports it.";

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
}

/// <summary>Fact that needs a validated host-state root; skipped with an explicit reason where owner validation is unavailable.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HostStateOwnerValidationFactAttribute : FactAttribute
{
    public HostStateOwnerValidationFactAttribute()
    {
        if (!HostStateOwnerValidation.IsSupported)
        {
            Skip = HostStateOwnerValidation.UnavailableSkipReason;
        }
    }
}

/// <summary>Theory that needs a validated host-state root; skipped with an explicit reason where owner validation is unavailable.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HostStateOwnerValidationTheoryAttribute : TheoryAttribute
{
    public HostStateOwnerValidationTheoryAttribute()
    {
        if (!HostStateOwnerValidation.IsSupported)
        {
            Skip = HostStateOwnerValidation.UnavailableSkipReason;
        }
    }
}

/// <summary>Fact that runs only where host-state owner validation is unavailable, to assert it fails closed.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HostStateOwnerValidationUnavailableFactAttribute : FactAttribute
{
    public HostStateOwnerValidationUnavailableFactAttribute()
    {
        if (HostStateOwnerValidation.IsSupported)
        {
            Skip = HostStateOwnerValidation.SupportedSkipReason;
        }
    }
}
