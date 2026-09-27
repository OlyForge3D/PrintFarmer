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
