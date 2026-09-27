using FluentAssertions;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Issue #3114: the host-update daemon and its identity are host-held. No canonical Compose template
/// (nor a generated root Compose file, when present) may mount, reference, or configure them, so a
/// container compromise cannot reach the daemon's key (docs/HOST_UPDATE_DAEMON_SECURITY.md#storage).
/// </summary>
public sealed class HostUpdateDaemonComposeTests
{
    private static readonly string[] ForbiddenFragments =
    [
        "HostUpdateDaemon",
        "printfarmer-host",
        "enrollment-key",
        "farm-host-update",
    ];

    [Fact]
    public void ComposeFiles_NeverReferenceTheHostUpdateDaemonOrItsIdentity()
    {
        string repositoryRoot = FindRepositoryRoot();
        string templates = Path.Combine(repositoryRoot, "scripts", "docker", "compose-templates");
        var files = Directory.EnumerateFiles(templates, "*.yml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(templates, "*.yaml", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(repositoryRoot, "docker-compose*.yml", SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(repositoryRoot, "compose*.yml", SearchOption.TopDirectoryOnly))
            .ToList();

        files.Should().Contain(path => Path.GetFileName(path) == "docker-compose.common.yml", "the canonical templates must be scanned");

        var offenders = files
            .SelectMany(path => ForbiddenFragments
                .Where(fragment => File.ReadAllText(path).Contains(fragment, StringComparison.OrdinalIgnoreCase))
                .Select(fragment => $"{Path.GetRelativePath(repositoryRoot, path)}: {fragment}"))
            .ToList();

        offenders.Should().BeEmpty();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "VERSION")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
