using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Focused coverage (issue #2663 / Kane audit P0.6) proving an owned directory that is
/// required-by-default fails the backup step closed when its configured source path is
/// missing, instead of silently recording a ".empty" sentinel and reporting success -- and that
/// only a directory explicitly opted into <see cref="HostUpdateExecutionOptions.OptionalOwnedDirectories"/>
/// keeps the previous empty-is-ok behavior.
/// </summary>
public sealed class HostUpdateBackupStepTests
{
    private static HostUpdateExecutionRequest Request() => new(
        "rel-1",
        1,
        "sha256:" + new string('a', 64),
        new string('b', 40),
        HostUpdateExecutionChannel.Stable,
        Enumerable.Range(1, 6).Select(i => new HostUpdateExecutionTarget("svc-" + i, "linux-amd64", "sha256:" + new string("abcdef"[i - 1], 64))).ToArray());

    [Fact]
    public async Task DirectoryCopyBackupTarget_RequiredAndMissing_ThrowsBackupIncomplete()
    {
        string missingSource = Path.Combine(Path.GetTempPath(), "hu-missing-" + Guid.NewGuid());
        var target = new DirectoryCopyBackupTarget("app-data", missingSource, isRequired: true);
        string destination = Path.Combine(Path.GetTempPath(), "hu-dest-" + Guid.NewGuid());
        Directory.CreateDirectory(destination);
        try
        {
            Func<Task> act = () => target.BackupAsync(destination, CancellationToken.None);
            await act.Should().ThrowAsync<HostUpdateBackupIncompleteException>();
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task DirectoryCopyBackupTarget_OptionalAndMissing_WritesEmptySentinelAndSucceeds()
    {
        string missingSource = Path.Combine(Path.GetTempPath(), "hu-missing-" + Guid.NewGuid());
        var target = new DirectoryCopyBackupTarget("certs", missingSource, isRequired: false);
        string destination = Path.Combine(Path.GetTempPath(), "hu-dest-" + Guid.NewGuid());
        Directory.CreateDirectory(destination);
        try
        {
            await target.BackupAsync(destination, CancellationToken.None);
            File.Exists(Path.Combine(destination, ".empty")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task DirectoryCopyBackupTarget_RequiredAndPresent_CopiesFilesNormally()
    {
        string source = Path.Combine(Path.GetTempPath(), "hu-src-" + Guid.NewGuid());
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "hello");
        var target = new DirectoryCopyBackupTarget("app-data", source, isRequired: true);
        string destination = Path.Combine(Path.GetTempPath(), "hu-dest-" + Guid.NewGuid());
        Directory.CreateDirectory(destination);
        try
        {
            await target.BackupAsync(destination, CancellationToken.None);
            File.Exists(Path.Combine(destination, "a.txt")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task BackupCoordinator_RequiredDirectoryWithZeroByteFile_WritesManifestInsteadOfFailingIncomplete()
    {
        string source = Path.Combine(Path.GetTempPath(), "hu-src-" + Guid.NewGuid());
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "empty.bin"), []);
        var target = new DirectoryCopyBackupTarget("app-data", source, isRequired: true);
        string root = Path.Combine(Path.GetTempPath(), "hu-root-" + Guid.NewGuid());
        var coordinator = new HostUpdateBackupCoordinator([target], root);
        try
        {
            await coordinator.RunAsync(Request(), CancellationToken.None);

            string manifest = Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories).Single();
            string json = await File.ReadAllTextAsync(manifest);
            json.Should().Contain("empty.bin");
            json.Should().Contain("\"Length\": 0");
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BackupCoordinator_RequiredDirectoryWithOnlyEmptyDirectories_WritesExplicitDirectoryCoverage()
    {
        string source = Path.Combine(Path.GetTempPath(), "hu-src-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(source, "profiles", "empty-child"));
        var target = new DirectoryCopyBackupTarget("profiles", source, isRequired: true);
        string root = Path.Combine(Path.GetTempPath(), "hu-root-" + Guid.NewGuid());
        var coordinator = new HostUpdateBackupCoordinator([target], root);
        try
        {
            await coordinator.RunAsync(Request(), CancellationToken.None);

            string directoryManifest = Directory.EnumerateFiles(root, ".printfarmer-directories.json", SearchOption.AllDirectories).Single();
            string json = await File.ReadAllTextAsync(directoryManifest);
            json.Should().Contain("profiles");
            json.Should().Contain("empty-child");
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
    [Fact]
    public async Task BackupCoordinator_RequiredDirectoryMissing_FailsRunClosed()
    {
        string missingSource = Path.Combine(Path.GetTempPath(), "hu-missing-" + Guid.NewGuid());
        var target = new DirectoryCopyBackupTarget("app-data", missingSource, isRequired: true);
        string root = Path.Combine(Path.GetTempPath(), "hu-root-" + Guid.NewGuid());
        var coordinator = new HostUpdateBackupCoordinator([target], root);
        try
        {
            Func<Task> act = () => coordinator.RunAsync(Request(), CancellationToken.None);
            await act.Should().ThrowAsync<HostUpdateBackupIncompleteException>();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
