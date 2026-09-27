using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>Issue #3114: host identity key storage permission validation (docs/HOST_UPDATE_DAEMON_SECURITY.md#storage).</summary>
public sealed class HostUpdateDaemonIdentityStorageTests : IDisposable
{
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly DirectoryInfo root = HostStateTestPaths.CreateTempSubdirectory("pf-daemon-identity-");

    private string IdentityDirectory => Path.Combine(root.FullName, "identity");

    private string ExecutorRoot => Path.Combine(root.FullName, "executor");

    private string KeyPath => Path.Combine(IdentityDirectory, HostUpdateDaemonIdentityStorage.KeyFileName);

    public void Dispose()
    {
        try
        {
            root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unconfigured_IsNotConfigured(string? directory)
    {
        var status = HostUpdateDaemonIdentityStorage.Inspect(directory, ExecutorRoot, isLinux: true, _ => true);

        status.State.Should().Be(HostUpdateDaemonIdentityStorageState.NotConfigured);
        status.Code.Should().Be("identity_not_configured");
        status.UsableForEnrollment.Should().BeFalse();
    }

    [Fact]
    public void RelativePath_IsRejected()
    {
        var status = HostUpdateDaemonIdentityStorage.Inspect("identity", ExecutorRoot, isLinux: true, _ => true);

        status.Should().Be(new HostUpdateDaemonIdentityStorageStatus(HostUpdateDaemonIdentityStorageState.Invalid, "identity_path_not_absolute"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("keys")]
    public void PathInsideExecutorRoot_IsRejected_SoBackupsNeverCopyTheKey(string child)
    {
        string directory = child.Length == 0 ? ExecutorRoot : Path.Combine(ExecutorRoot, child);

        var status = HostUpdateDaemonIdentityStorage.Inspect(directory, ExecutorRoot, isLinux: true, _ => true);

        status.Code.Should().Be("identity_inside_executor_root");
        status.State.Should().Be(HostUpdateDaemonIdentityStorageState.Invalid);
    }

    [Fact]
    public void SiblingWithSharedPrefix_IsNotTreatedAsInsideExecutorRoot()
    {
        var status = HostUpdateDaemonIdentityStorage.Inspect(ExecutorRoot + "-identity", ExecutorRoot, isLinux: true, _ => true);

        status.Code.Should().Be("identity_absent");
    }

    [Fact]
    public void NonLinuxPlatform_FailsClosed()
    {
        CreateIdentity();

        var status = HostUpdateDaemonIdentityStorage.Inspect(IdentityDirectory, ExecutorRoot, isLinux: false, _ => true);

        status.Should().Be(new HostUpdateDaemonIdentityStorageStatus(HostUpdateDaemonIdentityStorageState.Invalid, "identity_storage_platform_unsupported"));
    }

    [Fact]
    public void MissingDirectory_IsAbsent()
    {
        var status = HostUpdateDaemonIdentityStorage.Inspect(IdentityDirectory, ExecutorRoot, isLinux: true, _ => true);

        status.Should().Be(new HostUpdateDaemonIdentityStorageStatus(HostUpdateDaemonIdentityStorageState.Absent, "identity_absent"));
    }

    [Fact]
    public void MissingKey_IsAbsent()
    {
        CreateIdentity(writeKey: false);

        var status = HostUpdateDaemonIdentityStorage.Inspect(IdentityDirectory, ExecutorRoot, isLinux: true, _ => true);

        status.Should().Be(new HostUpdateDaemonIdentityStorageStatus(HostUpdateDaemonIdentityStorageState.Absent, "identity_key_absent"));
    }

    [Fact]
    public void DirectoryOwnedByAnotherUser_IsRejected()
    {
        CreateIdentity();

        var status = HostUpdateDaemonIdentityStorage.Inspect(IdentityDirectory, ExecutorRoot, isLinux: true, path => path != IdentityDirectory);

        status.Code.Should().Be("identity_directory_owner_mismatch");
    }

    [Fact]
    public void KeyOwnedByAnotherUser_IsRejected()
    {
        CreateIdentity();

        var status = HostUpdateDaemonIdentityStorage.Inspect(IdentityDirectory, ExecutorRoot, isLinux: true, path => path != KeyPath);

        status.Code.Should().Be("identity_key_owner_mismatch");
    }

    [Fact]
    public void KeyThatIsADirectory_IsRejected()
    {
        CreateIdentity(writeKey: false);
        Directory.CreateDirectory(KeyPath);

        var status = HostUpdateDaemonIdentityStorage.Inspect(IdentityDirectory, ExecutorRoot, isLinux: true, _ => true);

        status.Code.Should().Be("identity_key_not_regular_file");
    }

    [Fact]
    public void Status_NeverCarriesPathsOrKeyMaterial()
    {
        CreateIdentity();

        var status = HostUpdateDaemonIdentityStorage.Inspect(IdentityDirectory, ExecutorRoot, isLinux: true, path => path != KeyPath);

        status.ToString().Should().NotContain(root.FullName).And.NotContain("PRIVATE KEY");
    }

    [LinuxOnlyFact]
    public void Linux_OwnerOnlyStorage_IsValid()
    {
        CreateIdentity();

        var status = HostUpdateDaemonIdentityStorage.Inspect(IdentityDirectory, ExecutorRoot);

        status.Should().Be(new HostUpdateDaemonIdentityStorageStatus(HostUpdateDaemonIdentityStorageState.Valid, "identity_storage_valid"));
        status.UsableForEnrollment.Should().BeTrue();
    }

    [LinuxOnlyTheory]
    [InlineData(UnixFileMode.GroupRead | UnixFileMode.GroupExecute)]
    [InlineData(UnixFileMode.OtherRead | UnixFileMode.OtherExecute)]
    [InlineData(UnixFileMode.GroupWrite)]
    public void Linux_GroupOrOtherAccessToDirectory_IsRejected(UnixFileMode extra)
    {
        CreateIdentity();
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(IdentityDirectory, OwnerOnlyDirectory | extra);
        }

        var status = HostUpdateDaemonIdentityStorage.Inspect(IdentityDirectory, ExecutorRoot);

        status.Code.Should().Be("identity_directory_permissions_insecure");
    }

    [LinuxOnlyTheory]
    [InlineData(UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.UserExecute)]
    public void Linux_KeyWiderThan0600_IsRejected(UnixFileMode extra)
    {
        CreateIdentity();
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(KeyPath, OwnerOnlyFile | extra);
        }

        var status = HostUpdateDaemonIdentityStorage.Inspect(IdentityDirectory, ExecutorRoot);

        status.Code.Should().Be("identity_key_permissions_insecure");
    }

    [LinuxOnlyFact]
    public void Linux_SymlinkedKey_IsRejected()
    {
        CreateIdentity(writeKey: false);
        string target = Path.Combine(root.FullName, "elsewhere.pem");
        File.WriteAllText(target, "not a key");
        File.CreateSymbolicLink(KeyPath, target);

        var status = HostUpdateDaemonIdentityStorage.Inspect(IdentityDirectory, ExecutorRoot);

        status.Code.Should().Be("identity_reparse_rejected");
    }

    [LinuxOnlyFact]
    public void Linux_SymlinkedDirectory_IsRejected()
    {
        string target = Path.Combine(root.FullName, "real-identity");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(IdentityDirectory, target);

        var status = HostUpdateDaemonIdentityStorage.Inspect(IdentityDirectory, ExecutorRoot);

        status.Code.Should().Be("identity_reparse_rejected");
    }

    private void CreateIdentity(bool writeKey = true)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(IdentityDirectory);
        }
        else
        {
            Directory.CreateDirectory(IdentityDirectory, OwnerOnlyDirectory);
        }

        if (!writeKey)
        {
            return;
        }

        File.WriteAllText(KeyPath, "placeholder");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(KeyPath, OwnerOnlyFile);
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux-only: host identity storage is qualified on Linux only; other platforms fail closed (identity_storage_platform_unsupported).";
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LinuxOnlyTheoryAttribute : TheoryAttribute
{
    public LinuxOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux-only: host identity storage is qualified on Linux only; other platforms fail closed (identity_storage_platform_unsupported).";
        }
    }
}
