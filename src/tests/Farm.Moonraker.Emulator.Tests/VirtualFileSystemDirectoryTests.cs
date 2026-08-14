using Farm.Moonraker.Emulator.Domain;
using FluentAssertions;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

/// <summary>
/// Direct unit coverage of <see cref="VirtualFileSystem"/>'s explicit directory state
/// (<see cref="VirtualFileSystem.CreateDirectory"/>/<see cref="VirtualFileSystem.DeleteDirectory"/>),
/// including the "delete the root itself" edge case that isn't reachable unambiguously through the
/// REST route's existing root-prefixed path convention (see <c>SplitRootPath</c> in
/// <c>MoonrakerRestEndpoints</c>, which treats a slash-free value as a bare filename under the
/// default "gcodes" root rather than a bare root name).
/// </summary>
public sealed class VirtualFileSystemDirectoryTests
{
    [Fact]
    public void CreateDirectory_NewPath_SucceedsAndIsDiscoverableEvenWhenEmpty()
    {
        var fs = new VirtualFileSystem();

        fs.CreateDirectory("gcodes", "empty-dir").Should().Be(DirectoryCreateResult.Created);

        fs.DirectoryExists("gcodes", "empty-dir").Should().BeTrue();
        (IReadOnlyList<string> dirs, IReadOnlyList<VirtualFile> files) = fs.ListDirectory("gcodes", string.Empty);
        dirs.Should().Contain("empty-dir");

        (IReadOnlyList<string> childDirs, IReadOnlyList<VirtualFile> childFiles) = fs.ListDirectory("gcodes", "empty-dir");
        childDirs.Should().BeEmpty();
        childFiles.Should().BeEmpty();
    }

    [Fact]
    public void CreateDirectory_PathAlreadyExistsAsDirectory_ReturnsAlreadyExists()
    {
        var fs = new VirtualFileSystem();
        fs.CreateDirectory("gcodes", "dup-dir").Should().Be(DirectoryCreateResult.Created);

        fs.CreateDirectory("gcodes", "dup-dir").Should().Be(DirectoryCreateResult.AlreadyExists);
    }

    [Fact]
    public void CreateDirectory_PathAlreadyExistsAsFile_ReturnsAlreadyExists()
    {
        var fs = new VirtualFileSystem();
        fs.Put("gcodes", "taken.gcode", "content"u8.ToArray());

        fs.CreateDirectory("gcodes", "taken.gcode").Should().Be(DirectoryCreateResult.AlreadyExists);
    }

    [Fact]
    public void CreateDirectory_ImplicitDirectoryFromExistingFile_ReturnsAlreadyExists()
    {
        // "sub" is never explicitly created, but a file already lives under it, so Moonraker
        // still considers "sub" to be an existing directory.
        var fs = new VirtualFileSystem();
        fs.Put("gcodes", "sub/inner.gcode", "content"u8.ToArray());

        fs.CreateDirectory("gcodes", "sub").Should().Be(DirectoryCreateResult.AlreadyExists);
    }

    [Fact]
    public void CreateDirectory_ParentDoesNotExist_ReturnsParentMissing()
    {
        var fs = new VirtualFileSystem();

        fs.CreateDirectory("gcodes", "missing-parent/child").Should().Be(DirectoryCreateResult.ParentMissing);
    }

    [Fact]
    public void CreateDirectory_ParentExists_NestedCreateSucceeds()
    {
        var fs = new VirtualFileSystem();
        fs.CreateDirectory("gcodes", "parent").Should().Be(DirectoryCreateResult.Created);

        fs.CreateDirectory("gcodes", "parent/child").Should().Be(DirectoryCreateResult.Created);

        (IReadOnlyList<string> dirs, _) = fs.ListDirectory("gcodes", "parent");
        dirs.Should().Contain("child");
    }

    [Fact]
    public void DeleteDirectory_UnknownPath_ReturnsNotFound()
    {
        var fs = new VirtualFileSystem();

        fs.DeleteDirectory("gcodes", "does-not-exist", force: false).Should().Be(DirectoryDeleteResult.NotFound);
    }

    [Fact]
    public void DeleteDirectory_EmptyDirectory_Succeeds()
    {
        var fs = new VirtualFileSystem();
        fs.CreateDirectory("gcodes", "to-delete").Should().Be(DirectoryCreateResult.Created);

        fs.DeleteDirectory("gcodes", "to-delete", force: false).Should().Be(DirectoryDeleteResult.Deleted);

        fs.DirectoryExists("gcodes", "to-delete").Should().BeFalse();
    }

    [Fact]
    public void DeleteDirectory_NonEmptyWithoutForce_ReturnsNotEmptyAndLeavesContentIntact()
    {
        var fs = new VirtualFileSystem();
        fs.Put("gcodes", "nonempty/inner.gcode", "content"u8.ToArray());

        fs.DeleteDirectory("gcodes", "nonempty", force: false).Should().Be(DirectoryDeleteResult.NotEmpty);

        fs.TryGet("gcodes", "nonempty/inner.gcode", out VirtualFile? file).Should().BeTrue();
        file.Should().NotBeNull();
    }

    [Fact]
    public void DeleteDirectory_NonEmptyWithForce_RemovesDirectoryAndAllContents()
    {
        var fs = new VirtualFileSystem();
        fs.Put("gcodes", "nonempty/inner.gcode", "content"u8.ToArray());
        fs.CreateDirectory("gcodes", "nonempty/empty-subdir").Should().Be(DirectoryCreateResult.Created);

        fs.DeleteDirectory("gcodes", "nonempty", force: true).Should().Be(DirectoryDeleteResult.Deleted);

        fs.TryGet("gcodes", "nonempty/inner.gcode", out _).Should().BeFalse();
        fs.DirectoryExists("gcodes", "nonempty/empty-subdir").Should().BeFalse();
        fs.DirectoryExists("gcodes", "nonempty").Should().BeFalse();
    }

    [Fact]
    public void DeleteDirectory_RootItself_IsProtectedAndNeverDeleted()
    {
        var fs = new VirtualFileSystem();
        fs.Put("gcodes", "benchy.gcode", "content"u8.ToArray());

        fs.DeleteDirectory("gcodes", string.Empty, force: true).Should().Be(DirectoryDeleteResult.RootProtected);

        // The root's contents must be untouched even though force=true was requested.
        fs.TryGet("gcodes", "benchy.gcode", out _).Should().BeTrue();
    }
}
