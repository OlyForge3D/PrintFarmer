using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.FileManagement;

public class FileManagementServiceTests
{
    private static FileManagementService CreateSut() => new();

    [Fact]
    public void ResolveVirtualPath_NormalizesSegmentsAndLeadingSlash()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        FileManagementService sut = CreateSut();

        try
        {
            (string storageRoot, string resolved, string normalized) = sut.ResolveVirtualPath("folder/./sub/../file", root);

            storageRoot.Should().Be(root);
            resolved.Should().Be(Path.GetFullPath(Path.Combine(root, "folder", "sub", "file")));
            normalized.Should().Be("/folder/sub/file");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SanitizeFileName_ReplacesInvalidCharsAndAppendsExtension()
    {
        FileManagementService sut = CreateSut();

        string result = sut.SanitizeFileName("inv/valid", ".stl");

        result.Should().Be("inv_valid.stl");
    }

    [Fact]
    public void ResolveUniqueFileName_WhenCollision_AppendsCounter()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        string proposed = "model.stl";
        File.WriteAllText(Path.Combine(root, proposed), "content");
        FileManagementService sut = CreateSut();

        try
        {
            string unique = sut.ResolveUniqueFileName(root, proposed);

            unique.Should().Be("model (1).stl");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResolveUniqueFileName_WhenNoCollision_ReturnsOriginal()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        string proposed = "fresh.3mf";
        FileManagementService sut = CreateSut();

        try
        {
            string unique = sut.ResolveUniqueFileName(root, proposed);

            unique.Should().Be(proposed);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ComputeFileHashAsync_WithSha256_ReturnsExpectedHash()
    {
        string filePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        await File.WriteAllTextAsync(filePath, "abc");
        FileManagementService sut = CreateSut();

        try
        {
            string hash = await sut.ComputeFileHashAsync(filePath, "sha256");
            byte[] expectedBytes = SHA256.HashData(Encoding.UTF8.GetBytes("abc"));
            string expected = string.Concat(expectedBytes.Select(b => b.ToString("x2")));

            hash.Should().Be(expected);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ComputeFileHashAsync_WithSha1_ReturnsExpectedHash()
    {
        string filePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        await File.WriteAllTextAsync(filePath, "data");
        FileManagementService sut = CreateSut();

        try
        {
            string hash = await sut.ComputeFileHashAsync(filePath, "sha1");
            byte[] expectedBytes = SHA1.HashData(Encoding.UTF8.GetBytes("data"));
            string expected = string.Concat(expectedBytes.Select(b => b.ToString("x2")));

            hash.Should().Be(expected);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ComputeFileHashAsync_WithUnsupportedAlgorithm_Throws()
    {
        string filePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        await File.WriteAllTextAsync(filePath, "abc");
        FileManagementService sut = CreateSut();

        try
        {
            Func<Task> act = () => sut.ComputeFileHashAsync(filePath, "md5");

            await act.Should().ThrowAsync<ArgumentException>();
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ComputeFileHashAsync_WhenFileMissing_ThrowsFileNotFound()
    {
        FileManagementService sut = CreateSut();

        Func<Task> act = () => sut.ComputeFileHashAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), "sha256");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public void ToHex_ReturnsLowerCaseHex()
    {
        FileManagementService sut = CreateSut();

        string hex = sut.ToHex(new byte[] { 0x0f, 0xa0, 0x1b });

        hex.Should().Be("0fa01b");
    }

    [Fact]
    public void GenerateETag_ReturnsStrongAndWeakValues()
    {
        string filePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        File.WriteAllText(filePath, "etag");
        DateTime timestamp = new DateTime(2024, 01, 02, 03, 04, 05, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(filePath, timestamp);
        FileInfo info = new(filePath);
        info.Refresh();
        FileManagementService sut = CreateSut();

        try
        {
            string core = $"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}";

            sut.GenerateETag(info).Should().Be($"\"{core}\"");
            sut.GenerateETag(info, weak: true).Should().Be($"W/\"{core}\"");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void IsSafePath_ReturnsTrueWhenInsideRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        string candidate = Path.Combine(root, "nested", "file.txt");
        FileManagementService sut = CreateSut();

        try
        {
            bool result = sut.IsSafePath(candidate, root);

            result.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void IsSafePath_ReturnsFalseWhenOutsideRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        string candidate = "/etc/passwd";
        FileManagementService sut = CreateSut();

        try
        {
            bool result = sut.IsSafePath(candidate, root);

            result.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetModelFileFormat_ReturnsExpectedEnum()
    {
        FileManagementService sut = CreateSut();

        sut.GetModelFileFormat(".3mf").Should().Be(ModelFileFormat.TMF);
        sut.GetModelFileFormat("OBJ").Should().Be(ModelFileFormat.OBJ);
        sut.GetModelFileFormat("unknown").Should().Be(ModelFileFormat.STL);
        sut.GetModelFileFormat(string.Empty).Should().Be(ModelFileFormat.STL);
    }

    [Fact]
    public void GetModelFileFormatString_ReturnsLowerCaseExtension()
    {
        FileManagementService sut = CreateSut();

        sut.GetModelFileFormatString(ModelFileFormat.PLY).Should().Be("ply");
        sut.GetModelFileFormatString(ModelFileFormat.STEP).Should().Be("step");
    }

    [Fact]
    public void GetAllowedModelExtensions_ReturnsExpectedSet()
    {
        FileManagementService sut = CreateSut();

        sut.GetAllowedModelExtensions().Should().BeEquivalentTo(new[] { ".stl", ".3mf", ".obj", ".ply", ".step" });
    }

    [Fact]
    public void ValidateModelExtension_WhenValid_DoesNotThrow()
    {
        FileManagementService sut = CreateSut();

        System.Action act = () => sut.ValidateModelExtension("stl");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateModelExtension_WhenInvalid_Throws()
    {
        FileManagementService sut = CreateSut();

        System.Action act = () => sut.ValidateModelExtension(".exe");

        act.Should().Throw<ArgumentException>();
    }
}
