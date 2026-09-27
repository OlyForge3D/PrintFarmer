using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Tests.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates.PullApproval;

public sealed class HostUpdateInstallationIdentityStrictReadTests
{
    [Fact]
    public void MissingIdentity_ReturnsNull_WithoutCreatingIt()
    {
        WithRoot(root =>
        {
            HostStatePath paths = HostStatePath.OpenReadOnly(OptionsFor(root));

            Assert.Null(HostUpdateInstallationIdentity.ReadExistingStrict(paths));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        });
    }

    [Fact]
    public void CanonicalIdentity_IsReturned()
    {
        WithRoot(root =>
        {
            string identity = new('a', 32);
            File.WriteAllText(Path.Combine(root, "installation.id"), identity + "\n");

            Assert.Equal(identity, HostUpdateInstallationIdentity.ReadExistingStrict(HostStatePath.OpenReadOnly(OptionsFor(root))));
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hex-identity")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void NonCanonicalIdentity_ReturnsNull(string contents)
    {
        WithRoot(root =>
        {
            File.WriteAllText(Path.Combine(root, "installation.id"), contents);

            Assert.Null(HostUpdateInstallationIdentity.ReadExistingStrict(HostStatePath.OpenReadOnly(OptionsFor(root))));
        });
    }

    [Fact]
    public void UnreadableIdentity_ReturnsNull_WithoutMutation()
    {
        WithRoot(root =>
        {
            string path = Path.Combine(root, "installation.id");
            string identity = new('a', 32);
            File.WriteAllText(path, identity);
            HostStatePath paths = HostStatePath.OpenReadOnly(OptionsFor(root));

            if (OperatingSystem.IsWindows())
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.Null(HostUpdateInstallationIdentity.ReadExistingStrict(paths));
                }
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.None);
                try
                {
                    string? result = HostUpdateInstallationIdentity.ReadExistingStrict(paths);
                    if (Environment.UserName != "root")
                    {
                        Assert.Null(result);
                    }
                }
                finally
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }

            Assert.Equal(identity, File.ReadAllText(path));
            Assert.Single(Directory.EnumerateFileSystemEntries(root));
        });
    }

    [Fact]
    public void SymlinkedIdentity_ReturnsNull_WithoutMutation()
    {
        string targetRoot = HostStateTestPaths.CreateTempSubdirectory("printfarmer-strict-identity-target-").FullName;
        try
        {
            string target = Path.Combine(targetRoot, "installation.id");
            string identity = new('b', 32);
            File.WriteAllText(target, identity);
            WithRoot(root =>
            {
                string link = Path.Combine(root, "installation.id");
                try
                {
                    File.CreateSymbolicLink(link, target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Symlink creation needs developer mode or elevation on Windows; nothing to prove here.
                    return;
                }

                Assert.Null(HostUpdateInstallationIdentity.ReadExistingStrict(HostStatePath.OpenReadOnly(OptionsFor(root))));
                Assert.Equal(identity, File.ReadAllText(target));
            });
        }
        finally
        {
            Directory.Delete(targetRoot, true);
        }
    }

    private static HostStateOptions OptionsFor(string root) => new()
    {
        Enabled = true,
        RootPath = root,
        WindowsSecurityAttested = OperatingSystem.IsWindows(),
    };

    private static void WithRoot(Action<string> test)
    {
        string root = HostStateTestPaths.CreateTempSubdirectory("printfarmer-strict-identity-").FullName;
        try
        {
            test(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
